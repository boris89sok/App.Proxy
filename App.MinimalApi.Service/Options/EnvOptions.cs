namespace App.MinimalApi.Service.Options
{
    /// <summary>
    /// The application's single options class, bound from the <c>Proxy</c> configuration section
    /// (operational settings of the secure proxy; contains no secrets).
    /// DEFECT FIX: ValidateDataAnnotations()/ValidateOnStart() were configured but the class had no
    /// annotations, so invalid values were never detected at startup.
    /// </summary>
    public class EnvOptions
    {
        public const string ProxySectionName = "Proxy";

        /// <summary>Hard cap for any request envelope, applied before the operation is known.</summary>
        [Range(1024, 10_485_760)]
        public int GlobalMaxRequestBytes { get; set; } = 262_144;

        /// <summary>TTL of cached operation/destination configuration snapshots. 0 disables caching.</summary>
        [Range(0, 3600)]
        public int ConfigCacheSeconds { get; set; } = 30;

        /// <summary>TTL of cached "operation not found" results (protects the DB from key enumeration). 0 disables.</summary>
        [Range(0, 3600)]
        public int NegativeCacheSeconds { get; set; } = 5;

        /// <summary>Allows http:// upstreams. Must stay false outside local development.</summary>
        public bool AllowHttpUpstream { get; set; }

        // ValidateObjectMembers: without it the [Range] rules of the nested sections were never checked.
        [Required, ValidateObjectMembers]
        public AuditOptions Audit { get; set; } = new();

        [Required, ValidateObjectMembers]
        public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
    }

    public enum AuditMode
    {
        /// <summary>Audit rows are queued and written asynchronously; SQL outages never block traffic.</summary>
        BestEffort,

        /// <summary>An audit row must be persisted before a request is forwarded; otherwise the request fails with 503.</summary>
        Mandatory
    }

    /// <summary><c>Proxy:Audit</c></summary>
    public sealed class AuditOptions
    {
        public AuditMode Mode { get; set; } = AuditMode.BestEffort;

        [Range(10, 1_000_000)]
        public int ChannelCapacity { get; set; } = 10_000;

        [Range(1, 1000)]
        public int BatchSize { get; set; } = 100;

        [Range(0, 10)]
        public int MaxWriteAttempts { get; set; } = 3;
    }

    /// <summary><c>Proxy:CircuitBreaker</c></summary>
    public sealed class CircuitBreakerOptions
    {
        [Range(0.01, 1.0)]
        public double FailureRatio { get; set; } = 0.5;

        [Range(2, 10_000)]
        public int MinimumThroughput { get; set; } = 20;

        [Range(1, 3600)]
        public int SamplingDurationSeconds { get; set; } = 30;

        [Range(1, 3600)]
        public int BreakDurationSeconds { get; set; } = 15;
    }
}
