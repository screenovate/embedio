﻿namespace Unosquare.Labs.EmbedIO
{
    using Constants;
    using Swan;
    using System;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    internal class HttpHandler
    {
        private readonly IHttpContext _context;
        private string _requestId = "(not set)";

        public HttpHandler(IHttpContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Handles the client request.
        /// </summary>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous of client request.</returns>
        public async Task HandleClientRequest(CancellationToken ct)
        {
            try
            {
                // Create a request endpoint string
                var requestEndpoint =
                    $"{_context.Request?.RemoteEndPoint?.Address}:{_context.Request?.RemoteEndPoint?.Port}";

                // Generate a random request ID. It's currently not important but could be useful in the future.
                _requestId = string.Concat(DateTime.Now.Ticks.ToString(), requestEndpoint).GetHashCode().ToString("x2");

                // Log the request and its ID
                $"Start of Request {_requestId} - Source {requestEndpoint} - {_context.RequestVerb().ToString().ToUpperInvariant()}: {_context.Request.Url.PathAndQuery} - {_context.Request.UserAgent}"
                    .Debug(nameof(HttpHandler));

                var processResult = await ProcessRequest(ct).ConfigureAwait(false);

                // Return a 404 (Not Found) response if no module/handler handled the response.
                if (processResult == false)
                {
                    "No module generated a response. Sending 404 - Not Found".Error(nameof(HttpHandler));

                    if (_context.WebServer.OnNotFound == null)
                    {
                        _context.Response.StandardResponseWithoutBody((int) HttpStatusCode.NotFound);
                    }
                    else
                    {
                        await _context.WebServer.OnNotFound(_context).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                ex.Log(nameof(HttpHandler), "Error handling request.");
            }
            finally
            {
                // Always close the response stream no matter what.
                _context?.Response.Close();

                $"End of Request {_requestId}".Debug(nameof(HttpHandler));
            }
        }

        private async Task<bool> ProcessRequest(CancellationToken ct)
        {
            // Iterate though the loaded modules to match up a request and possibly generate a response.
            foreach (var module in _context.WebServer.Modules)
            {
                var callback = GetHandler(module);

                if (callback == null) continue;

                try
                {
                    // Log the module and handler to be called and invoke as a callback.
                    $"{module.Name}::{callback.GetMethodInfo().DeclaringType?.Name}.{callback.GetMethodInfo().Name}"
                        .Debug(nameof(HttpHandler));

                    // Execute the callback
                    var handleResult = await callback(_context, ct).ConfigureAwait(false);

                    $"Result: {handleResult}".Trace(nameof(HttpHandler));

                    // callbacks can instruct the server to stop bubbling the request through the rest of the modules by returning true;
                    if (handleResult)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // Handle exceptions by returning a 500 (Internal Server Error) 
                    if (_context.Response.StatusCode != (int) HttpStatusCode.Unauthorized)
                    {
                        await ResponseServerError(ct, ex).ConfigureAwait(false);
                    }

                    // Finally set the handled flag to true and exit.
                    return true;
                }
            }

            return false;
        }

        private async Task ResponseServerError(CancellationToken cancellationToken, Exception ex)
        {
            if (_context.WebServer.UnhandledException != null && await _context.WebServer.UnhandledException.Invoke(_context, ex, cancellationToken))
                return;

            // Send the response over with the corresponding status code.
            await _context.Response.StandardHtmlResponseAsync(
                (int) HttpStatusCode.InternalServerError,
                sb => sb
                    .Append("<h2>Message</h2><pre>")
                    .Append(ex.ExceptionMessage())
                    .Append("</pre><h2>Stack Trace</h2><pre>\r\n")
                    .Append(ex.StackTrace)
                    .Append("</pre>"),
                cancellationToken).ConfigureAwait(false);
        }

        private Map GetHandlerFromRegexPath(IWebModule module)
            => module.Handlers.FirstOrDefault(x =>
                (x.Path == ModuleMap.AnyPath || _context.RequestRegexUrlParams(x.Path) != null) &&
                (x.Verb == HttpVerbs.Any || x.Verb == _context.RequestVerb()));

        private Map GetHandlerFromWildcardPath(IWebModule module)
        {
            var path = _context.RequestWilcardPath(module.Handlers
                .Where(k => k.Path.Contains(ModuleMap.AnyPathRoute))
                .Select(s => s.Path.ToLowerInvariant()));

            return module.Handlers
                .FirstOrDefault(x =>
                    (x.Path == ModuleMap.AnyPath || x.Path == path) &&
                    (x.Verb == HttpVerbs.Any || x.Verb == _context.RequestVerb()));
        }

        private WebHandler GetHandler(IWebModule module)
        {
            Map handler;

            switch (_context.WebServer.RoutingStrategy)
            {
                case RoutingStrategy.Wildcard:
                    handler = GetHandlerFromWildcardPath(module);
                    break;
                case RoutingStrategy.Regex:
                    handler = GetHandlerFromRegexPath(module);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(RoutingStrategy));
            }

            return handler?.ResponseHandler;
        }
    }
}