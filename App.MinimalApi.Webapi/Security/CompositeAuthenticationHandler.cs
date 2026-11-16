namespace App.MinimalApi.Webapi.Security;

/// <summary>
/// Succeeds only when BOTH the API key scheme and the JWT bearer scheme succeed, and merges their identities.
/// Missing or invalid either credential -> authentication fails -> 401 (not 403), and the request never
/// reaches the proxy pipeline.
/// </summary>
public sealed class CompositeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKeyAndJwt";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKey = await Context.AuthenticateAsync(SecurityPolicies.ApiKeyScheme);
        var jwt = await Context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        if (!apiKey.Succeeded || !jwt.Succeeded)
        {
            return apiKey.None && jwt.None
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Fail("Both an API key and a bearer token are required.");
        }

        var principal = new ClaimsPrincipal(apiKey.Principal!.Identities.Concat(jwt.Principal!.Identities));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer, {SecurityPolicies.ApiKeyScheme} header=\"{ProxyHeaders.ApiKey}\"";
        return Task.CompletedTask;
    }
}
