namespace App.MinimalApi.Webapi.Endpoints;

public sealed class ProxyEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // The handler writes the response itself so upstream bodies can be streamed (bounded) without buffering.
        app.MapPost("/api/proxy/execute", (HttpContext context, [FromServices] IProxyExecutionService proxy) =>
                proxy.ExecuteAsync(context))
            .RequireAuthorization(SecurityPolicies.ProxyClient)
            .RequireRateLimiting(RateLimitingSetup.ProxyPolicy)
            // "*/*" keeps routing from rejecting other content types with an empty 415: the proxy pipeline
            // rejects them itself, with the standard error contract and an audit record.
            .Accepts<ProxyExecuteRequest>("application/json", "*/*")
            .Produces(StatusCodes.Status200OK)
            .Produces<ProxyErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ProxyErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ProxyErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ProxyErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ProxyErrorResponse>(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces<ProxyErrorResponse>(StatusCodes.Status502BadGateway)
            .Produces<ProxyErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces<ProxyErrorResponse>(StatusCodes.Status504GatewayTimeout)
            .WithName("ProxyExecute")
            .WithTags("Proxy");
    }
}
