using System.Diagnostics;

namespace App.MinimalApi.Service.Extensions
{
    /// <summary>
    /// Logs outbound call metadata only.
    /// DEFECT FIX: the previous version logged the full request URL (query strings may carry personal data or
    /// tokens) and the complete raw response body, and buffered the whole body with ReadAsStringAsync
    /// (unbounded memory use, breaks streaming).
    /// </summary>
    public class RawLoggingHandler(ILogger<RawLoggingHandler> logger) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var response = await base.SendAsync(request, cancellationToken);

            logger.LogInformation(
                "External API {Method} {Host} responded {StatusCode} in {ElapsedMs}ms",
                request.Method.Method,
                request.RequestUri?.Host,
                (int)response.StatusCode,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return response;
        }
    }
}
