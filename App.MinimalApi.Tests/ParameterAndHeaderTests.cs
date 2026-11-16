namespace App.MinimalApi.Tests;

/// <summary>Required cases 7, 8, 9, 10.</summary>
public sealed class ParameterAndHeaderTests(ProxyWebAppFactory factory) : ProxyTestBase(factory), IClassFixture<ProxyWebAppFactory>
{
    // 7
    [Fact]
    public async Task Missing_urlId_is_rejected()
    {
        var response = await SendAsync(new { clientId = "customer.get", json = new { id = 5 } });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidParameters);
        var error = await ReadErrorAsync(response);
        Assert.Contains("urlId: required", error.Errors!);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Missing_required_query_parameter_is_rejected()
    {
        var response = await SendAsync(new { clientId = "customer.search", json = new { urlId = 5 } });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidParameters);
        Assert.Contains("status: required", (await ReadErrorAsync(response)).Errors!);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("{\"urlId\":0}")]                 // below MinValue
    [InlineData("{\"urlId\":-3}")]
    [InlineData("{\"urlId\":2147483648}")]        // above MaxValue
    [InlineData("{\"urlId\":5.5}")]
    [InlineData("{\"urlId\":\"five\"}")]
    [InlineData("{\"urlId\":true}")]
    [InlineData("{\"urlId\":null}")]
    [InlineData("{\"urlId\":[5]}")]
    [InlineData("{\"urlId\":{\"$gt\":1}}")]
    public async Task Invalid_parameter_values_are_rejected(string json)
    {
        var response = await SendRawAsync($"{{\"clientId\":\"customer.get\",\"json\":{json}}}");

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidParameters);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("{\"urlId\":5,\"status\":\"pending\"}")]          // regex open|closed (full match)
    [InlineData("{\"urlId\":5,\"status\":\"open; drop\"}")]
    [InlineData("{\"urlId\":5,\"status\":\"open\",\"from\":\"31/01/2026\"}")] // date format
    public async Task Query_parameter_rule_violations_are_rejected(string json)
    {
        var response = await SendRawAsync($"{{\"clientId\":\"customer.search\",\"json\":{json}}}");

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidParameters);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Duplicate_parameter_names_are_rejected()
    {
        var response = await SendRawAsync("{\"clientId\":\"customer.get\",\"json\":{\"urlId\":5,\"urlId\":\"../admin\"}}");

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidEnvelope);
        AssertNotForwarded();
    }

    // 8
    [Fact]
    public async Task Unknown_parameters_are_rejected_when_the_operation_does_not_allow_them()
    {
        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5, debug = true } });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidParameters);
        Assert.Contains("debug: not allowed", (await ReadErrorAsync(response)).Errors!);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Unknown_parameters_are_dropped_not_forwarded_when_the_operation_allows_them()
    {
        var response = await SendAsync(new { clientId = "customer.search", json = new { urlId = 5, status = "closed", debug = true, admin = 1 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(Upstream.Requests);
        Assert.Equal("?status=closed", forwarded.Uri.Query);
        Assert.DoesNotContain("debug", forwarded.Uri.AbsoluteUri);
        Assert.DoesNotContain("admin", forwarded.Uri.AbsoluteUri);
    }

    // 9
    [Theory]
    [InlineData("../admin")]
    [InlineData("..")]
    [InlineData("a/../../etc/passwd")]
    [InlineData("..\\..\\windows")]
    [InlineData("%2e%2e%2fadmin")]
    [InlineData("%252e%252e%252f")]
    [InlineData("report?admin=true")]
    [InlineData("report#fragment")]
    [InlineData("a;b")]
    [InlineData("http:")]
    public async Task Path_traversal_attempts_are_rejected(string value)
    {
        var response = await SendAsync(new { clientId = "files.get", json = new { urlId = value } });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidPathParameter);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Path_traversal_through_typed_parameter_is_rejected()
    {
        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = "5/../../admin" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Safe_string_path_value_is_percent_encoded_into_a_single_segment()
    {
        var response = await SendAsync(new { clientId = "files.get", json = new { urlId = "report 2026.pdf" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(Upstream.Requests);
        Assert.Equal("/files/report%202026.pdf", forwarded.Uri.AbsolutePath);
    }

    // 10
    [Fact]
    public async Task Only_allowlisted_headers_are_forwarded()
    {
        var correlationId = ProxyWebAppFactory.NewCorrelationId();
        var request = ProxyWebAppFactory.Execute(new { clientId = "customer.get", json = new { urlId = 5 } }, correlationId);
        request.Headers.Add("X-Request-Source", "mobile-app");
        request.Headers.Add("Cookie", "session=abc");
        request.Headers.Add("X-Forwarded-For", "10.0.0.1");
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        request.Headers.Add("Proxy-Authorization", "Basic Zm9vOmJhcg==");
        request.Headers.Add("X-Internal-Secret", "s3cr3t");
        request.Headers.Add("Upgrade", "websocket");

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(Upstream.Requests);
        Assert.Equal(["mobile-app"], forwarded.Headers["X-Request-Source"]);
        Assert.Equal([correlationId], forwarded.Headers["X-Correlation-Id"]);

        // Authorization and Cookie are even in the operation's allowlist (seeded misconfiguration) - still blocked.
        foreach (var blocked in new[] { "Authorization", "Cookie", "X-Api-Key", "X-Forwarded-For", "X-Forwarded-Host",
                     "Proxy-Authorization", "X-Internal-Secret", "Upgrade", "Host" })
        {
            Assert.False(forwarded.Headers.ContainsKey(blocked), $"{blocked} must not be forwarded");
        }
        Assert.Equal(["X-Correlation-Id", "X-Request-Source"], forwarded.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public async Task Allowlisted_header_nominated_as_hop_by_hop_by_the_caller_is_not_forwarded()
    {
        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } }, r =>
        {
            r.Headers.Add("X-Request-Source", "mobile-app");
            r.Headers.Connection.Add("X-Request-Source");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Assert.Single(Upstream.Requests).Headers.ContainsKey("X-Request-Source"));
    }

    [Theory]
    [InlineData("mobile app")]          // regex
    [InlineData("<script>")]
    public async Task Invalid_allowlisted_header_value_is_rejected(string value)
    {
        var response = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } },
            r => r.Headers.TryAddWithoutValidation("X-Request-Source", value));

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidHeader);
        AssertNotForwarded();
    }
}
