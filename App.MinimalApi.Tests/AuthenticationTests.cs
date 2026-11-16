namespace App.MinimalApi.Tests;

/// <summary>Both X-Api-Key and a valid JWT bearer token are required.</summary>
public sealed class AuthenticationTests(ProxyWebAppFactory factory) : ProxyTestBase(factory), IClassFixture<ProxyWebAppFactory>
{
    private static readonly object Envelope = new { clientId = "customer.get", json = new { urlId = 5 } };

    private async Task<HttpStatusCode> SendWithAsync(string? apiKey, string? token)
    {
        var client = Factory.CreateClient();
        if (apiKey is not null) client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(ProxyWebAppFactory.Execute(Envelope))).StatusCode;
    }

    [Fact]
    public async Task Missing_both_credentials_is_rejected() =>
        await AssertUnauthorized(await SendWithAsync(null, null));

    [Fact]
    public async Task Api_key_without_jwt_is_rejected() =>
        await AssertUnauthorized(await SendWithAsync(TestSecrets.ApiKey, null));

    [Fact]
    public async Task Jwt_without_api_key_is_rejected() =>
        await AssertUnauthorized(await SendWithAsync(null, TestSecrets.CreateToken()));

    [Fact]
    public async Task Wrong_api_key_is_rejected() =>
        await AssertUnauthorized(await SendWithAsync("wrong-key", TestSecrets.CreateToken()));

    [Fact]
    public async Task Jwt_signed_with_another_key_is_rejected() =>
        await AssertUnauthorized(await SendWithAsync(TestSecrets.ApiKey, TestSecrets.CreateToken(signingKey: RandomNumberGenerator.GetBytes(32))));

    [Fact]
    public async Task Expired_jwt_is_rejected() =>
        await AssertUnauthorized(await SendWithAsync(TestSecrets.ApiKey, TestSecrets.CreateToken(lifetime: TimeSpan.FromMinutes(-5))));

    [Fact]
    public async Task Both_credentials_are_accepted()
    {
        Assert.Equal(HttpStatusCode.OK, await SendWithAsync(TestSecrets.ApiKey, TestSecrets.CreateToken()));
        Assert.Single(Upstream.Requests);
    }

    private Task AssertUnauthorized(HttpStatusCode status)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        AssertNotForwarded();
        return Task.CompletedTask;
    }
}
