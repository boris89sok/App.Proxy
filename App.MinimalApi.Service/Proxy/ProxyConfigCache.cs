namespace App.MinimalApi.Service.Proxy;

/// <summary>Immutable snapshot of the trusted configuration of one operation.</summary>
public sealed record OperationSnapshot(
    int Id,
    string Key,
    string HttpMethod,
    string PathTemplate,
    bool IsEnabled,
    int MaxRequestBytes,
    int MaxResponseBytes,
    bool AllowUnknownParameters,
    bool RequiresEncryption,
    EncryptionProfileSnapshot? EncryptionProfile,
    DestinationSnapshot? Destination,
    IReadOnlyList<ParameterRuleSnapshot> Parameters,
    IReadOnlyList<HeaderRuleSnapshot> Headers);

public sealed record DestinationSnapshot(
    int Id,
    string Name,
    string BaseUrl,
    bool IsEnabled,
    int TimeoutSeconds,
    string? AllowedPorts);

public sealed record ParameterRuleSnapshot(
    string Name,
    bool IsRequired,
    string DataType,
    int? MinLength,
    int? MaxLength,
    long? MinValue,
    long? MaxValue,
    string? RegexPattern,
    string Location);

public sealed record HeaderRuleSnapshot(string HeaderName, bool IsRequired, int? MaxLength, string? RegexPattern);

public sealed record EncryptionProfileSnapshot(int Id, string Name, string Algorithm, string KeyReference, int KeyVersion, bool IsEnabled);

public interface IProxyConfigCache
{
    /// <summary>Returns the configuration snapshot of an operation, or null when it does not exist.</summary>
    Task<OperationSnapshot?> GetOperationAsync(string operationKey, CancellationToken cancellationToken);

    void Invalidate(string operationKey);
}

/// <summary>
/// Read-through cache of operation configuration.
/// Consistency model: a change made through the admin API invalidates the entry on this instance
/// immediately; other instances (and direct SQL changes) converge within <see cref="EnvOptions.ConfigCacheSeconds"/>.
/// Each request works on the snapshot it obtained at its start (no mid-request config changes).
/// </summary>
public sealed class ProxyConfigCache(
    IMemoryCache cache,
    IOperationRepository operations,
    IOptions<EnvOptions> options) : IProxyConfigCache
{
    private sealed record Entry(OperationSnapshot? Value);

    private static string CacheKey(string operationKey) => "proxy:op:" + operationKey;

    public async Task<OperationSnapshot?> GetOperationAsync(string operationKey, CancellationToken cancellationToken)
    {
        var key = CacheKey(operationKey);
        if (cache.TryGetValue(key, out Entry? cached) && cached is not null)
        {
            return cached.Value;
        }

        var entity = await operations.GetByKeyWithGraphAsync(operationKey, cancellationToken);
        // Exact (ordinal) match: SQL Server collations are case-insensitive, the proxy is not.
        var snapshot = entity is null || !string.Equals(entity.OperationKey, operationKey, StringComparison.Ordinal)
            ? null
            : Map(entity);

        var ttl = snapshot is null ? options.Value.NegativeCacheSeconds : options.Value.ConfigCacheSeconds;
        if (ttl > 0)
        {
            cache.Set(key, new Entry(snapshot), TimeSpan.FromSeconds(ttl));
        }

        return snapshot;
    }

    public void Invalidate(string operationKey) => cache.Remove(CacheKey(operationKey));

    internal static OperationSnapshot Map(Operation o) => new(
        o.Id,
        o.OperationKey,
        o.HttpMethod,
        o.PathTemplate,
        o.IsEnabled,
        o.MaxRequestBytes,
        o.MaxResponseBytes,
        o.AllowUnknownParameters,
        o.RequiresEncryption,
        o.EncryptionProfile is null
            ? null
            : new EncryptionProfileSnapshot(o.EncryptionProfile.Id, o.EncryptionProfile.Name, o.EncryptionProfile.Algorithm,
                o.EncryptionProfile.KeyReference, o.EncryptionProfile.KeyVersion, o.EncryptionProfile.IsEnabled),
        o.Destination is null
            ? null
            : new DestinationSnapshot(o.Destination.Id, o.Destination.Name, o.Destination.BaseUrl, o.Destination.IsEnabled,
                o.Destination.TimeoutSeconds, o.Destination.AllowedPorts),
        o.ParameterRules
            .OrderBy(r => r.Id)
            .Select(r => new ParameterRuleSnapshot(r.Name, r.IsRequired, r.DataType, r.MinLength, r.MaxLength,
                r.MinValue, r.MaxValue, r.RegexPattern, r.Location))
            .ToArray(),
        o.HeaderRules
            .OrderBy(r => r.Id)
            .Select(r => new HeaderRuleSnapshot(r.HeaderName, r.IsRequired, r.MaxLength, r.RegexPattern))
            .ToArray());
}
