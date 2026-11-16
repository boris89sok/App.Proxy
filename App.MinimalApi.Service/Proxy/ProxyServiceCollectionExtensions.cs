namespace App.MinimalApi.Service.Proxy;

public static class ProxyServiceCollectionExtensions
{
    /// <summary>Name of the IHttpClientFactory client used for all upstream calls.</summary>
    public const string HttpClientName = "proxy-upstream";

    /// <summary>Proxy services. Its settings are part of <see cref="EnvOptions"/> (registered by AddServices).</summary>
    public static IServiceCollection AddSecureProxy(this IServiceCollection services, IConfiguration configuration)
    {
        // Connection string is resolved lazily so hosts/tests can override configuration.
        services.AddDbContext<SecureProxyContext>((sp, db) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("SecureProxyDb")
                ?? throw new InvalidOperationException("Connection string 'SecureProxyDb' is not configured.");
            // No EnableRetryOnFailure: the proxy never retries writes blindly (see DESIGN-NOTES "Retries").
            db.UseSqlServer(connectionString);
        });

        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IOperationRepository, OperationRepository>();
        services.AddScoped<IDestinationRepository, DestinationRepository>();
        services.AddScoped<IRequestLogRepository, RequestLogRepository>();

        services.AddScoped<IProxyConfigCache, ProxyConfigCache>();
        services.AddScoped<IProxyExecutionService, ProxyExecutionService>();
        services.AddScoped<IProxyConfigAdminService, ProxyConfigAdminService>();

        services.AddSingleton<ISecretKeyProvider, ConfigurationSecretKeyProvider>();
        services.AddSingleton<IPayloadDecryptor, AesGcmPayloadDecryptor>();
        services.AddSingleton<AuditChannel>();
        services.AddSingleton<IRequestAuditor, RequestAuditor>();
        services.AddHostedService<AuditWriterService>();

        services.AddHttpClient(ProxyServiceCollectionExtensions.HttpClientName, client =>
            {
                // Per-request timeouts come from Destination.TimeoutSeconds (linked CancellationTokenSource).
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,           // a redirect could point outside the configured destination
                UseCookies = false,                  // never share cookies between callers
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None, // size limits apply to bytes on the wire
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), // picks up DNS changes
                ConnectTimeout = TimeSpan.FromSeconds(10),
                MaxResponseHeadersLength = 64,       // KB
                // Do not let HttpClient inject W3C trace headers (traceparent/baggage): only allowlisted headers
                // plus X-Correlation-Id leave the proxy.
                ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
                SslOptions = new SslClientAuthenticationOptions() // default certificate validation stays ON
            })
            // Circuit breaker ONLY - no retry and no extra timeout strategy, so nothing can be sent twice.
            .AddResilienceHandler("upstream-circuit-breaker", (pipeline, context) =>
            {
                var breaker = context.ServiceProvider.GetRequiredService<IOptions<EnvOptions>>().Value.CircuitBreaker;
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = breaker.FailureRatio,
                    MinimumThroughput = breaker.MinimumThroughput,
                    SamplingDuration = TimeSpan.FromSeconds(breaker.SamplingDurationSeconds),
                    BreakDuration = TimeSpan.FromSeconds(breaker.BreakDurationSeconds)
                });
            })
            .SelectPipelineByAuthority(); // one breaker per destination authority

        return services;
    }
}
