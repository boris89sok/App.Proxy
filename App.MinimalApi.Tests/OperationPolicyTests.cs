namespace App.MinimalApi.Tests;

/// <summary>Required cases 2, 3, 4 and the envelope rules.</summary>
public sealed class OperationPolicyTests(ProxyWebAppFactory factory) : ProxyTestBase(factory), IClassFixture<ProxyWebAppFactory>
{
    // 2
    [Theory]
    [InlineData("customer.unknown")]
    [InlineData("CUSTOMER.GET")] // keys are matched exactly (ordinal)
    public async Task Unknown_operation_is_rejected(string clientId)
    {
        var response = await SendAsync(new { clientId, json = new { urlId = 5 } });

        await AssertProxyErrorAsync(response, 404, ProxyErrorCode.UnknownOperation);
        AssertNotForwarded();
    }

    // 3
    [Fact]
    public async Task Disabled_operation_is_rejected()
    {
        var response = await SendAsync(new { clientId = "customer.delete-all", json = new { urlId = 5 } });

        await AssertProxyErrorAsync(response, 403, ProxyErrorCode.OperationDisabled);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Operation_disabled_after_it_worked_is_rejected_for_new_requests()
    {
        Assert.Equal(200, (int)(await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } })).StatusCode);
        Upstream.Reset();

        Factory.WithDb(db =>
        {
            db.Operations.Single(o => o.OperationKey == "customer.get").IsEnabled = false;
            return db.SaveChanges();
        });

        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } });
        await AssertProxyErrorAsync(response, 403, ProxyErrorCode.OperationDisabled);
        AssertNotForwarded();
    }

    // 4
    [Fact]
    public async Task Disabled_destination_is_rejected()
    {
        var response = await SendAsync(new { clientId = "legacy.get", json = new { urlId = 5 } });

        await AssertProxyErrorAsync(response, 503, ProxyErrorCode.DestinationUnavailable);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Missing_destination_is_rejected()
    {
        // An operation whose destination cannot be resolved (e.g. row removed out of band) - full pipeline.
        using var app = Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<IProxyConfigCache, FakeCacheWithoutDestination>()));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestSecrets.ApiKey);
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestSecrets.CreateToken());

        var response = await client.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "orphan.get", json = new { urlId = 5 } }));

        await AssertProxyErrorAsync(response, 503, ProxyErrorCode.DestinationUnavailable);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/json; charset=utf-16")]
    public async Task Invalid_content_type_is_rejected(string contentType)
    {
        var response = await SendRawAsync("{\"clientId\":\"customer.get\",\"json\":{\"urlId\":5}}", contentType);

        await AssertProxyErrorAsync(response, 415, ProxyErrorCode.UnsupportedMediaType);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_larger_than_the_global_limit_is_rejected()
    {
        var padding = new string('a', 300_000);
        var response = await SendRawAsync($"{{\"clientId\":\"customer.get\",\"json\":{{\"urlId\":5,\"p\":\"{padding}\"}}}}");

        await AssertProxyErrorAsync(response, 413, ProxyErrorCode.RequestTooLarge);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_larger_than_the_operation_limit_is_rejected()
    {
        var padding = new string('a', 70_000); // operation MaxRequestBytes = 65536 < global 262144
        var response = await SendRawAsync($"{{\"clientId\":\"customer.get\",\"json\":{{\"urlId\":5,\"p\":\"{padding}\"}}}}");

        await AssertProxyErrorAsync(response, 413, ProxyErrorCode.RequestTooLarge);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"json\":{\"urlId\":5}}")]
    [InlineData("{\"clientId\":\"customer.get\"}")]
    [InlineData("{\"clientId\":\"customer.get\",\"json\":\"urlId=5\"}")]
    [InlineData("{\"clientId\":\"customer.get\",\"clientId\":\"customer.purge\",\"json\":{\"urlId\":5}}")]
    [InlineData("{\"clientId\":\"../customer.get\",\"json\":{\"urlId\":5}}")]
    [InlineData("{\"clientId\":\"customer.get\",\"json\":{\"urlId\":5},}")]
    public async Task Invalid_envelope_is_rejected(string body)
    {
        var response = await SendRawAsync(body);

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidEnvelope);
        AssertNotForwarded();
    }

    private sealed class FakeCacheWithoutDestination : IProxyConfigCache
    {
        public Task<OperationSnapshot?> GetOperationAsync(string operationKey, CancellationToken cancellationToken) =>
            Task.FromResult<OperationSnapshot?>(new(99, operationKey, "GET", "/x/{urlId}", true, 65536, 65536, false, false, null, null,
                [new ParameterRuleSnapshot("urlId", true, "int", null, null, 1, null, null, "Path")], []));

        public void Invalidate(string operationKey) { }
    }
}
