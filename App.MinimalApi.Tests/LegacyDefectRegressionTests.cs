namespace App.MinimalApi.Tests;

/// <summary>
/// Regression tests for the defects in DEFECT-REPORT.md whose code is still part of the solution
/// (the Region sample was removed as out of scope; its defects are documented as resolved by removal).
/// </summary>
public sealed class LegacyDefectRegressionTests
{
    [Fact] // D-01: TLS certificate validation was disabled for the Refit client
    public void Refit_client_primary_handler_keeps_certificate_validation()
    {
        using var handler = HttpClientExtension.CreatePrimaryHandler();

        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact] // D-02: RawLoggingHandler read and logged the full response body
    public async Task Raw_logging_handler_does_not_consume_the_response_body()
    {
        var content = new NonReadableContent();
        var handler = new RawLoggingHandler(NullLogger<RawLoggingHandler>.Instance)
        {
            InnerHandler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
        };

        using var invoker = new HttpMessageInvoker(handler);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.org/x?token=secret"), default);

        Assert.False(content.WasRead);
    }

    [Fact] // D-06: deleting a missing id threw ArgumentNullException (500) instead of a not-found result
    public async Task Generic_repository_delete_of_missing_id_throws_KeyNotFoundException()
    {
        await using var db = NewContext();
        var repository = new DestinationRepository(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.DeleteAsync(999));
        Assert.Throws<KeyNotFoundException>(() => repository.Delete(999));
    }

    [Fact] // D-07: every update called ChangeTracker.Clear() and silently discarded other pending changes
    public async Task Generic_repository_update_keeps_unrelated_pending_changes()
    {
        var name = "repo-" + Guid.NewGuid().ToString("N");
        await using (var seed = NewContext(name))
        {
            seed.Destinations.AddRange(NewDestination(1, "A"), NewDestination(2, "B"));
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext(name))
        {
            var a = await db.Destinations.SingleAsync(d => d.Id == 1);
            a.TimeoutSeconds = 99;                                   // pending, not saved yet
            await db.Destinations.SingleAsync(d => d.Id == 2);       // tracked instance with the same key as the update

            var detachedB = NewDestination(2, "B-renamed");
            await new DestinationRepository(db).UpdateAsync(detachedB);
        }

        await using (var verify = NewContext(name))
        {
            Assert.Equal(99, (await verify.Destinations.SingleAsync(d => d.Id == 1)).TimeoutSeconds);
            Assert.Equal("B-renamed", (await verify.Destinations.SingleAsync(d => d.Id == 2)).Name);
        }
    }

    private static SecureProxyContext NewContext(string? name = null) =>
        new(new DbContextOptionsBuilder<SecureProxyContext>()
            .UseInMemoryDatabase(name ?? "repo-" + Guid.NewGuid().ToString("N"))
            .Options);

    private static Destination NewDestination(int id, string name) => new()
    {
        Id = id,
        Name = name,
        BaseUrl = "https://example.internal",
        IsEnabled = true,
        TimeoutSeconds = 10,
        CreatedAt = new DateTime(2026, 1, 1),
        UpdatedAt = new DateTime(2026, 1, 1),
        RowVersion = new byte[8]
    };

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class NonReadableContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            WasRead = true;
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

/// <summary>
/// D-03 / D-04: the global exception handler was never activated (UseExceptionHandler missing) and, once active,
/// returned exception.Message to callers. These tests throw through the REAL pipeline of Program.cs: a terminal
/// middleware appended after the app's own pipeline throws exceptions whose messages contain "internal" details.
/// </summary>
public sealed class GlobalExceptionHandlerPipelineTests(GlobalExceptionHandlerPipelineTests.ThrowingAppFactory factory)
    : IClassFixture<GlobalExceptionHandlerPipelineTests.ThrowingAppFactory>
{
    private const string Secret = "internal-db-01.corp.local";

    public sealed class ThrowingAppFactory : ProxyWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ThrowingStartupFilter>());
        }
    }

    private sealed class ThrowingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app); // the application's pipeline, including UseExceptionHandler
            app.Run(context => context.Request.Path.Value switch
            {
                "/__defect-test/not-found" => throw new KeyNotFoundException($"Row 42 on {Secret} was not found"),
                "/__defect-test/upstream" => throw new HttpRequestException($"Connection refused by {Secret}"),
                _ => throw new InvalidOperationException($"Login failed for user 'sa' on {Secret}")
            });
        };
    }

    [Theory]
    [InlineData("/__defect-test/not-found", HttpStatusCode.NotFound, ProxyErrorCode.NotFound)]
    [InlineData("/__defect-test/upstream", HttpStatusCode.BadGateway, ProxyErrorCode.UpstreamUnavailable)]
    [InlineData("/__defect-test/unexpected", HttpStatusCode.InternalServerError, ProxyErrorCode.InternalError)]
    public async Task Unhandled_exceptions_return_a_generic_error_without_internal_details(string path, HttpStatusCode expected, ProxyErrorCode code)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Same error contract as the proxy itself (one shape for the whole API).
        var error = System.Text.Json.JsonSerializer.Deserialize<ProxyErrorResponse>(body)!;
        Assert.Equal(code.ToString(), error.Code);
        Assert.Equal("proxy", error.Source);
        Assert.False(string.IsNullOrEmpty(error.CorrelationId));
        Assert.DoesNotContain(Secret, body);  // exception.Message is not exposed
        Assert.DoesNotContain("   at ", body); // no stack trace
        Assert.DoesNotContain("Exception", body);
    }
}

/// <summary>Startup validation of the single options class (EnvOptions), including its nested Proxy sections.</summary>
public sealed class OptionsValidationTests
{
    private sealed class InvalidOptionsFactory(string key, string value) : ProxyWebAppFactory
    {
        protected override IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { [key] = value };
    }

    [Theory]
    [InlineData("Proxy:ConfigCacheSeconds", "-1")]
    [InlineData("Proxy:CircuitBreaker:FailureRatio", "5")] // nested section: was never validated before
    [InlineData("Proxy:Audit:BatchSize", "0")]
    public void Out_of_range_settings_fail_at_startup(string key, string value)
    {
        using var factory = new InvalidOptionsFactory(key, value);

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(key.Split(':')[^1], error.ToString());
    }
}
