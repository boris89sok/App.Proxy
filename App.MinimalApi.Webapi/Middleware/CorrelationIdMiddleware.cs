namespace App.MinimalApi.Webapi.Middleware;

/// <summary>
/// Gives every request a correlation id: a well-formed incoming <c>X-Correlation-Id</c> is reused
/// (end-to-end tracing), otherwise a new one is generated. The id becomes HttpContext.TraceIdentifier,
/// is pushed into the Serilog LogContext, returned to the caller, forwarded upstream and stored in RequestLogs.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[ProxyHeaders.CorrelationId];
        var id = incoming.Count == 1 && ValidId().IsMatch(incoming[0]!) ? incoming[0]! : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ProxyHeaders.CorrelationId] = id;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}
