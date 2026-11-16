namespace App.MinimalApi.Tests;

/// <summary>Required cases 1, 5, 6, 13, 14, 15.</summary>
public sealed class ProxyForwardingTests(ProxyWebAppFactory factory) : ProxyTestBase(factory), IClassFixture<ProxyWebAppFactory>
{
    // 1
    [Fact]
    public async Task Valid_configured_operation_is_forwarded_to_the_configured_destination()
    {
        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"id\":5,\"name\":\"Ada\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal("upstream", response.Headers.GetValues("X-Proxy-Source").Single());

        var forwarded = Assert.Single(Upstream.Requests);
        Assert.Equal(HttpMethod.Get, forwarded.Method);
        Assert.Equal(new Uri("https://customers.internal:5001/customers/5"), forwarded.Uri);
        Assert.False(forwarded.HadBody);

        // upstream response headers are filtered
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Query_parameters_are_canonicalised_and_built_from_configuration()
    {
        var response = await SendAsync(new { clientId = "customer.search", json = new { urlId = "+0007", status = "open", from = "2026-01-31" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(Upstream.Requests);
        Assert.Equal("https://customers.internal:5001/customers/7/orders?status=open&from=2026-01-31", forwarded.Uri.AbsoluteUri);
    }

    // 5
    [Theory]
    [InlineData("url", "https://evil.example/steal")]
    [InlineData("destination", "https://evil.example")]
    [InlineData("baseUrl", "https://evil.example")]
    [InlineData("path", "/admin/delete")]
    [InlineData("headers", "Authorization: x")]
    public async Task Caller_supplied_destination_fields_in_envelope_are_rejected(string field, string value)
    {
        var envelope = new Dictionary<string, object>
        {
            ["clientId"] = "customer.get",
            ["json"] = new { urlId = 5 },
            [field] = value
        };

        var response = await SendAsync(envelope);

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.UnknownEnvelopeField);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Caller_supplied_url_inside_parameters_is_rejected_when_unknown_parameters_are_not_allowed()
    {
        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5, url = "https://evil.example" } });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidParameters);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Caller_supplied_url_inside_parameters_cannot_change_destination_when_unknown_parameters_are_allowed()
    {
        var response = await SendAsync(new
        {
            clientId = "customer.search",
            json = new { urlId = 5, status = "open", url = "https://evil.example", host = "evil.example" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(Upstream.Requests);
        Assert.Equal("customers.internal", forwarded.Uri.Host);
        Assert.Equal("https://customers.internal:5001/customers/5/orders?status=open", forwarded.Uri.AbsoluteUri);
        Assert.DoesNotContain("evil", forwarded.Uri.AbsoluteUri);
    }

    // 6
    [Theory]
    [InlineData("method", "DELETE")]
    [InlineData("httpMethod", "POST")]
    public async Task Caller_supplied_http_method_is_rejected(string field, string value)
    {
        var envelope = new Dictionary<string, object> { ["clientId"] = "customer.get", ["json"] = new { urlId = 5 }, [field] = value };

        var response = await SendAsync(envelope);

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.UnknownEnvelopeField);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Proxy_endpoint_only_accepts_POST(string method)
    {
        var request = ProxyWebAppFactory.Execute(new { clientId = "customer.get", json = new { urlId = 5 } });
        request.Method = new HttpMethod(method);

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Operation_configured_with_a_disallowed_http_method_fails_closed()
    {
        var response = await SendAsync(new { clientId = "customer.purge", json = new { urlId = 5 } });

        await AssertProxyErrorAsync(response, 500, ProxyErrorCode.OperationMisconfigured);
        AssertNotForwarded();
    }

    // 13
    [Fact]
    public async Task Upstream_timeout_returns_504_gateway_timeout()
    {
        Upstream.Responder = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return RecordingHandler.Json(HttpStatusCode.OK, "{}");
        };
        var correlationId = ProxyWebAppFactory.NewCorrelationId();

        var response = await Client.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "slow.get", json = new { urlId = 1 } }, correlationId));

        await AssertProxyErrorAsync(response, 504, ProxyErrorCode.UpstreamTimeout);
        Assert.Single(Upstream.Requests);
        var audit = await Factory.WaitForAuditAsync(correlationId);
        Assert.Equal("Timeout", audit.Outcome);
        Assert.True(audit.Forwarded);
        Assert.Equal(504, audit.ProxyStatus);
    }

    // 14
    [Fact]
    public async Task Upstream_4xx_is_relayed_with_its_status_and_marked_as_upstream()
    {
        Upstream.Responder = (_, _) => Task.FromResult(RecordingHandler.Json(HttpStatusCode.NotFound, "{\"error\":\"customer not found\"}"));

        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 404 } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("upstream", response.Headers.GetValues("X-Proxy-Source").Single());
        Assert.Equal("{\"error\":\"customer not found\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upstream_5xx_status_is_preserved_but_its_body_is_not_leaked()
    {
        Upstream.Responder = (_, _) => Task.FromResult(RecordingHandler.Json(HttpStatusCode.ServiceUnavailable,
            "System.Exception at Customers.Db (Server=sql-prod-01.corp.local)"));

        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("upstream", response.Headers.GetValues("X-Proxy-Source").Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("corp.local", body);
        var error = await ReadErrorAsync(response);
        Assert.Equal(ProxyErrorCode.UpstreamError.ToString(), error.Code);
        Assert.Equal("upstream", error.Source);
        Assert.Equal(503, error.UpstreamStatus);
    }

    [Fact]
    public async Task Upstream_connection_failure_returns_502_without_internal_details()
    {
        Upstream.Responder = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "No connection could be made to customers.internal:5001");

        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } });

        await AssertProxyErrorAsync(response, 502, ProxyErrorCode.UpstreamUnavailable);
        Assert.DoesNotContain("customers.internal", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upstream_response_larger_than_the_configured_limit_is_rejected()
    {
        Upstream.Responder = (_, _) => Task.FromResult(RecordingHandler.Json(HttpStatusCode.OK, new string('x', 500)));

        var response = await SendAsync(new { clientId = "customer.small", json = new { urlId = 5 } });

        await AssertProxyErrorAsync(response, 502, ProxyErrorCode.UpstreamResponseTooLarge);
    }

    // 15
    [Fact]
    public async Task Caller_cancellation_is_propagated_to_the_upstream_call()
    {
        var upstreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Upstream.Responder = async (_, ct) =>
        {
            upstreamStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return RecordingHandler.Json(HttpStatusCode.OK, "{}");
        };
        var correlationId = ProxyWebAppFactory.NewCorrelationId();
        using var callerCts = new CancellationTokenSource();

        var call = Client.SendAsync(
            ProxyWebAppFactory.Execute(new { clientId = "customer.get", json = new { urlId = 5 } }, correlationId),
            callerCts.Token);
        await upstreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await Upstream.UpstreamCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var audit = await Factory.WaitForAuditAsync(correlationId);
        Assert.Equal("ClientCancelled", audit.Outcome);
        Assert.Equal(499, audit.ProxyStatus);
    }
}
