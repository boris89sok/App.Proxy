namespace App.MinimalApi.Webapi.Security;

public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>Token bucket size per API client (burst).</summary>
    [Range(1, 1_000_000)]
    public int TokenLimit { get; set; } = 100;

    /// <summary>Tokens added per second per API client (sustained rate).</summary>
    [Range(1, 1_000_000)]
    public int TokensPerSecond { get; set; } = 50;

    /// <summary>Bounded concurrency for the whole instance (protects threads, sockets and the upstreams).</summary>
    [Range(1, 100_000)]
    public int MaxConcurrentRequests { get; set; } = 200;
}

public static class RateLimitingSetup
{
    public const string ProxyPolicy = "proxy";

    public static IServiceCollection AddProxyRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<RateLimitSettings>(configuration.GetSection(RateLimitSettings.SectionName));

        services.AddRateLimiter(_ => { });
        // Settings are read once from the validated options; the partition factories capture only those
        // values (capturing HttpContext would keep the first request's context alive for the app's lifetime).
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitSettings>>((options, settingsOptions) =>
        {
            var settings = settingsOptions.Value;
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Instance-wide bounded concurrency; excess requests are rejected immediately (no queue build-up).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = settings.MaxConcurrentRequests,
                    QueueLimit = 0
                }));

            // Per API client token bucket (the client identity comes from the X-Api-Key authentication).
            options.AddPolicy(ProxyPolicy, context =>
            {
                var client = context.User.GetIdentity(SecurityPolicies.ApiKeyScheme)?.Name
                    ?? "anonymous:" + context.Connection.RemoteIpAddress;
                return RateLimitPartition.GetTokenBucketLimiter(client, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = settings.TokenLimit,
                    TokensPerPeriod = settings.TokensPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        return services;
    }
}
