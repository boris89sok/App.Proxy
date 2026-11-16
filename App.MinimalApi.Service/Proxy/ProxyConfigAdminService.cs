namespace App.MinimalApi.Service.Proxy;

public interface IProxyConfigAdminService
{
    Task<OperationAdminDto?> GetOperationAsync(int id, CancellationToken cancellationToken);

    /// <exception cref="ProxyRejectedException">
    /// <see cref="ProxyErrorCode.InvalidParameters"/> (invalid values), <see cref="ProxyErrorCode.NotFound"/> (unknown id) or
    /// <see cref="ProxyErrorCode.ConfigurationConflict"/> (the row changed since <paramref name="expectedRowVersion"/> was read).
    /// </exception>
    Task<OperationAdminDto> UpdateOperationAsync(int id, OperationUpdateDto update, byte[] expectedRowVersion, string? user, CancellationToken cancellationToken);

    Task<DestinationAdminDto?> GetDestinationAsync(int id, CancellationToken cancellationToken);

    /// <inheritdoc cref="UpdateOperationAsync"/>
    Task<DestinationAdminDto> UpdateDestinationAsync(int id, DestinationUpdateDto update, byte[] expectedRowVersion, string? user, CancellationToken cancellationToken);
}

/// <summary>
/// Configuration updates with optimistic concurrency (SQL Server rowversion).
/// A stale rowversion produces <see cref="ProxyErrorCode.ConfigurationConflict"/> (HTTP 409) and nothing is written.
/// Successful updates invalidate the local configuration cache; SQL Server temporal tables record history.
/// </summary>
public sealed class ProxyConfigAdminService(
    IOperationRepository operations,
    IDestinationRepository destinations,
    IProxyConfigCache cache,
    IOptions<EnvOptions> options,
    TimeProvider timeProvider,
    ILogger<ProxyConfigAdminService> logger) : IProxyConfigAdminService
{
    public async Task<OperationAdminDto?> GetOperationAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await operations.GetNoTrackingAsync(o => o.Id == id);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<OperationAdminDto> UpdateOperationAsync(
        int id, OperationUpdateDto update, byte[] expectedRowVersion, string? user, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (!UpstreamRequestBuilder.IsValidPathTemplate(update.PathTemplate)) errors.Add("pathTemplate: invalid");
        if (update.MaxRequestBytes < 1 || update.MaxRequestBytes > options.Value.GlobalMaxRequestBytes) errors.Add("maxRequestBytes: out of range");
        if (update.MaxResponseBytes is < 1 or > ProxyLimits.MaxResponseBytes) errors.Add("maxResponseBytes: out of range");

        var entity = await UpdateAsync(operations, id, expectedRowVersion, errors, o =>
        {
            o.IsEnabled = update.IsEnabled;
            o.PathTemplate = update.PathTemplate;
            o.MaxRequestBytes = update.MaxRequestBytes;
            o.MaxResponseBytes = update.MaxResponseBytes;
            o.AllowUnknownParameters = update.AllowUnknownParameters;
            o.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            o.UpdatedBy = user.Truncate(100);
        }, cancellationToken);

        cache.Invalidate(entity.OperationKey);
        logger.LogInformation("Operation {OperationId} {OperationKey} updated by {User} (enabled={IsEnabled})",
            entity.Id, entity.OperationKey, entity.UpdatedBy, entity.IsEnabled);
        return ToDto(entity);
    }

    public async Task<DestinationAdminDto?> GetDestinationAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await destinations.GetNoTrackingAsync(d => d.Id == id);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<DestinationAdminDto> UpdateDestinationAsync(
        int id, DestinationUpdateDto update, byte[] expectedRowVersion, string? user, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (!UpstreamRequestBuilder.TryParseBaseUrl(update.BaseUrl, options.Value.AllowHttpUpstream, out _)) errors.Add("baseUrl: invalid");
        if (!ProxyLimits.IsValidTimeout(update.TimeoutSeconds)) errors.Add("timeoutSeconds: out of range");
        if (!UpstreamRequestBuilder.TryParsePorts(update.AllowedPorts, out _)) errors.Add("allowedPorts: invalid");

        var entity = await UpdateAsync(destinations, id, expectedRowVersion, errors, d =>
        {
            d.IsEnabled = update.IsEnabled;
            d.BaseUrl = update.BaseUrl;
            d.TimeoutSeconds = update.TimeoutSeconds;
            d.AllowedPorts = update.AllowedPorts;
            d.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            d.UpdatedBy = user.Truncate(100);
        }, cancellationToken);

        foreach (var key in await operations.GetKeysByDestinationAsync(entity.Id, cancellationToken))
        {
            cache.Invalidate(key);
        }

        logger.LogInformation("Destination {DestinationId} updated by {User} (enabled={IsEnabled})",
            entity.Id, entity.UpdatedBy, entity.IsEnabled);
        return ToDto(entity);
    }

    /// <summary>Shared update flow: validate, load (tracked), expect the caller's rowversion, apply, save; stale -> 409.</summary>
    private static async Task<TEntity> UpdateAsync<TEntity>(
        IConfigRepository<TEntity> repository,
        int id,
        byte[] expectedRowVersion,
        List<string> errors,
        Action<TEntity> apply,
        CancellationToken cancellationToken) where TEntity : class
    {
        if (errors.Count > 0)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidParameters, "ConfigValidation", errors);
        }

        var entity = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new ProxyRejectedException(ProxyErrorCode.NotFound, "ConfigNotFound");

        repository.SetExpectedRowVersion(entity, expectedRowVersion);
        apply(entity);

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ProxyRejectedException(ProxyErrorCode.ConfigurationConflict, "ConfigConflict", inner: ex);
        }

        return entity;
    }

    private static OperationAdminDto ToDto(Operation o) => new()
    {
        Id = o.Id,
        OperationKey = o.OperationKey,
        DestinationId = o.DestinationId,
        HttpMethod = o.HttpMethod,
        PathTemplate = o.PathTemplate,
        IsEnabled = o.IsEnabled,
        MaxRequestBytes = o.MaxRequestBytes,
        MaxResponseBytes = o.MaxResponseBytes,
        AllowUnknownParameters = o.AllowUnknownParameters,
        RequiresEncryption = o.RequiresEncryption,
        EncryptionProfileId = o.EncryptionProfileId,
        UpdatedAt = o.UpdatedAt,
        UpdatedBy = o.UpdatedBy,
        RowVersion = Convert.ToBase64String(o.RowVersion ?? [])
    };

    private static DestinationAdminDto ToDto(Destination d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        BaseUrl = d.BaseUrl,
        IsEnabled = d.IsEnabled,
        TimeoutSeconds = d.TimeoutSeconds,
        AllowedPorts = d.AllowedPorts,
        UpdatedAt = d.UpdatedAt,
        UpdatedBy = d.UpdatedBy,
        RowVersion = Convert.ToBase64String(d.RowVersion ?? [])
    };
}
