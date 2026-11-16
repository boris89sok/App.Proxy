namespace App.MinimalApi.Tests.Infrastructure;

/// <summary>
/// Hosts the real Webapi pipeline (auth, rate limiting, middleware, proxy) in memory.
/// Replaced only: SQL Server -> EF InMemory, upstream network -> <see cref="RecordingHandler"/>.
/// </summary>
public class ProxyWebAppFactory : WebApplicationFactory<Program>
{
    private readonly InMemoryDatabaseRoot _dbRoot = new();
    private readonly string _dbName = "proxy-" + Guid.NewGuid().ToString("N");

    public RecordingHandler Upstream { get; } = new();

    /// <summary>Extra configuration a derived factory can apply (e.g. Mandatory audit mode).</summary>
    protected virtual IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:SecureProxyDb"] = "Server=unused-in-tests",
                ["Security:Jwt:Issuer"] = TestSecrets.Issuer,
                ["Security:Jwt:Audience"] = TestSecrets.Audience,
                ["Security:Jwt:SigningKey"] = Convert.ToBase64String(TestSecrets.SigningKey),
                ["Security:ApiClients:0:Name"] = TestSecrets.ApiClientName,
                ["Security:ApiClients:0:ApiKeySha256"] = TestSecrets.ApiKeyHash,                [$"Proxy:EncryptionKeys:{TestSecrets.EncryptionKeyReference}:v1"] = Convert.ToBase64String(TestSecrets.EncryptionKey),
                ["Proxy:ConfigCacheSeconds"] = "0",
                ["Proxy:NegativeCacheSeconds"] = "0",
                ["Proxy:CircuitBreaker:MinimumThroughput"] = "10000",
                ["RateLimiting:TokenLimit"] = "100000",
                ["RateLimiting:TokensPerSecond"] = "100000",
                ["RateLimiting:MaxConcurrentRequests"] = "1000"
            };
            foreach (var (key, value) in ExtraSettings) settings[key] = value;
            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<SecureProxyContext>>();

            services.AddDbContext<SecureProxyContext>(o => o
                .UseInMemoryDatabase(_dbName, _dbRoot)
                .AddInterceptors(new RowVersionSimulationInterceptor())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            // The mock destination: every upstream call ends here instead of the network.
            services.AddHttpClient(ProxyServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }

    /// <summary>Resets the recorded upstream traffic and re-creates the seeded configuration.</summary>
    public void Reset()
    {
        Upstream.Reset();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureProxyContext>();
        db.Database.EnsureDeleted();
        TestData.Seed(db);
    }

    public T WithDb<T>(Func<SecureProxyContext, T> action)
    {
        using var scope = Services.CreateScope();
        return action(scope.ServiceProvider.GetRequiredService<SecureProxyContext>());
    }

    /// <summary>Client carrying a valid API key and JWT (both are required).</summary>
    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", TestSecrets.ApiKey);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecrets.CreateToken());
        return client;
    }

    public static HttpRequestMessage Execute(object envelope, string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/proxy/execute")
        {
            Content = JsonContent.Create(envelope)
        };
        request.Headers.Add("X-Correlation-Id", correlationId ?? NewCorrelationId());
        return request;
    }

    public static HttpRequestMessage ExecuteRaw(string json, string contentType = "application/json", string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/proxy/execute")
        {
            Content = new StringContent(json, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Add("X-Correlation-Id", correlationId ?? NewCorrelationId());
        return request;
    }

    public static string NewCorrelationId() => "test-" + Guid.NewGuid().ToString("N");

    /// <summary>Audit rows are written asynchronously (BestEffort policy): poll until the row appears.</summary>
    public async Task<RequestLog> WaitForAuditAsync(string correlationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var row = WithDb(db => db.RequestLogs.AsNoTracking().FirstOrDefault(r => r.CorrelationId == correlationId));
            if (row is not null) return row;
            await Task.Delay(50);
        }
        throw new TimeoutException($"No audit row for {correlationId}");
    }
}

/// <summary>
/// EF InMemory does not generate SQL Server rowversion values. This interceptor emulates them
/// (new value on every insert/update) so optimistic concurrency behaves like SQL Server in tests.
/// </summary>
public sealed class RowVersionSimulationInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Stamp(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var property = entry.Metadata.FindProperty("RowVersion");
            if (property is null) continue;
            entry.Property("RowVersion").CurrentValue = Guid.NewGuid().ToByteArray()[..8];
        }
    }
}
