namespace App.MinimalApi.Webapi.Security;

/// <summary>
/// Authenticates the calling application with the <c>X-Api-Key</c> header.
/// The presented key is hashed (SHA-256) and compared in constant time with the configured hashes.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<SecuritySettings> settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const int MaxKeyLength = 256;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ProxyHeaders.ApiKey, out var values) || values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrEmpty(presented) || presented.Length > MaxKeyLength)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        ApiClientSettings? match = null;
        foreach (var client in settings.CurrentValue.ApiClients)
        {
            // Compare against every client (no early exit) to keep timing independent of the position.
            var expected = Convert.FromHexString(client.ApiKeySha256);
            if (CryptographicOperations.FixedTimeEquals(presentedHash, expected) && match is null)
            {
                match = client;
            }
        }

        if (match is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, match.Name), new Claim("api_client", match.Name)],
            SecurityPolicies.ApiKeyScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SecurityPolicies.ApiKeyScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    public static string HashKey(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
