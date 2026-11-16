namespace App.MinimalApi.Tests.Infrastructure;

public abstract class ProxyTestBase
{
    protected ProxyTestBase(ProxyWebAppFactory factory)
    {
        Factory = factory;
        Factory.Reset();
        Client = factory.CreateAuthorizedClient();
    }

    protected ProxyWebAppFactory Factory { get; }
    protected HttpClient Client { get; }
    protected RecordingHandler Upstream => Factory.Upstream;

    protected Task<HttpResponseMessage> SendAsync(object envelope, Action<HttpRequestMessage>? configure = null, CancellationToken cancellationToken = default)
    {
        var request = ProxyWebAppFactory.Execute(envelope);
        configure?.Invoke(request);
        return Client.SendAsync(request, cancellationToken);
    }

    protected Task<HttpResponseMessage> SendRawAsync(string json, string contentType = "application/json") =>
        Client.SendAsync(ProxyWebAppFactory.ExecuteRaw(json, contentType));

    /// <summary>The decisive assertion for every rejection: the destination received nothing.</summary>
    protected void AssertNotForwarded() => Assert.Empty(Upstream.Requests);

    protected static async Task<ProxyErrorResponse> ReadErrorAsync(HttpResponseMessage response)
    {
        // ReadAsStringAsync works on buffered content and can be called repeatedly (stream reads cannot).
        var error = System.Text.Json.JsonSerializer.Deserialize<ProxyErrorResponse>(await response.Content.ReadAsStringAsync());
        Assert.NotNull(error);
        return error!;
    }

    protected static async Task AssertProxyErrorAsync(HttpResponseMessage response, int status, ProxyErrorCode code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        var error = await ReadErrorAsync(response);
        Assert.Equal(code.ToString(), error.Code);
        Assert.Equal("proxy", error.Source);
        Assert.Equal("proxy", response.Headers.GetValues("X-Proxy-Source").Single());
        Assert.False(string.IsNullOrEmpty(error.CorrelationId));
    }
}
