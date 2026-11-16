namespace App.MinimalApi.Webapi.Exceptions
{
    /// <summary>
    /// Converts unhandled exceptions into the application's single, NON-SENSITIVE error contract
    /// (<see cref="ProxyErrors"/>: same status table, messages and body as the proxy itself).
    /// DEFECT FIXES:
    /// - exception.Message was returned to callers (leaked internal details: SQL, hostnames, paths);
    /// - HttpRequestException (upstream failure) was reported as 400 (caller error) instead of 502;
    /// - the handler was registered but never activated (UseExceptionHandler was missing in Program.cs),
    ///   so in Development the developer exception page with stack traces was shown instead.
    /// The full exception is logged server-side with the correlation id.
    /// </summary>
    public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            {
                // The caller went away: nothing to write, not a server error.
                logger.LogInformation("Request {CorrelationId} cancelled by the caller", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = ProxyErrors.ClientClosedRequest;
                return true;
            }

            var code = exception switch
            {
                ProxyRejectedException rejected => rejected.Code,
                KeyNotFoundException => ProxyErrorCode.NotFound,
                BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => ProxyErrorCode.RequestTooLarge,
                BadHttpRequestException or ArgumentException => ProxyErrorCode.InvalidRequest,
                DbUpdateConcurrencyException => ProxyErrorCode.ConfigurationConflict,
                UnauthorizedAccessException => ProxyErrorCode.Forbidden,
                HttpRequestException => ProxyErrorCode.UpstreamUnavailable,
                _ => ProxyErrorCode.InternalError
            };
            var status = ProxyErrors.StatusFor(code);

            if (status >= 500)
            {
                logger.LogError(exception, "Unhandled exception {ExceptionType} for {Method} {Path} -> {StatusCode}",
                    exception.GetType().Name, httpContext.Request.Method, httpContext.Request.Path, status);
            }
            else
            {
                logger.LogWarning("Request failed with {ExceptionType} for {Method} {Path} -> {StatusCode}",
                    exception.GetType().Name, httpContext.Request.Method, httpContext.Request.Path, status);
            }

            await ProxyErrors.WriteAsync(httpContext, code, (exception as ProxyRejectedException)?.Errors,
                cancellationToken: cancellationToken);
            return true;
        }
    }
}
