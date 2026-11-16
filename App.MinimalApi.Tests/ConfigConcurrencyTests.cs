namespace App.MinimalApi.Tests;

/// <summary>
/// Required case 16 - optimistic concurrency on configuration (SQL Server rowversion).
/// Configuration is not exposed over HTTP (the spec requires only <c>POST /api/proxy/execute</c>);
/// updates go through <see cref="IProxyConfigAdminService"/>, which maps a stale rowversion to
/// <see cref="ProxyErrorCode.ConfigurationConflict"/> (HTTP 409 in the shared error contract).
/// </summary>
public sealed class ConfigConcurrencyTests(ProxyWebAppFactory factory) : ProxyTestBase(factory), IClassFixture<ProxyWebAppFactory>
{
    private const string Admin = "alice";

    private async Task<T> WithAdminAsync<T>(Func<IProxyConfigAdminService, Task<T>> action)
    {
        // A new scope per call = a new DbContext, like two separate admin requests.
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IProxyConfigAdminService>());
    }

    private Task<OperationAdminDto> GetOperationAsync() =>
        WithAdminAsync(async s => (await s.GetOperationAsync(TestData.CustomerGetOperationId, CancellationToken.None))!);

    private Task<OperationAdminDto> UpdateOperationAsync(OperationAdminDto read, OperationUpdateDto update) =>
        WithAdminAsync(s => s.UpdateOperationAsync(read.Id, update, Convert.FromBase64String(read.RowVersion), Admin, CancellationToken.None));

    private static OperationUpdateDto UpdateFrom(OperationAdminDto dto, bool enabled) => new()
    {
        IsEnabled = enabled,
        PathTemplate = dto.PathTemplate,
        MaxRequestBytes = dto.MaxRequestBytes,
        MaxResponseBytes = dto.MaxResponseBytes,
        AllowUnknownParameters = dto.AllowUnknownParameters
    };

    private static DestinationUpdateDto UpdateFrom(DestinationAdminDto dto, bool enabled) => new()
    {
        IsEnabled = enabled,
        BaseUrl = dto.BaseUrl,
        TimeoutSeconds = dto.TimeoutSeconds,
        AllowedPorts = dto.AllowedPorts
    };

    [Fact]
    public async Task Update_with_current_rowversion_succeeds_and_returns_a_new_rowversion()
    {
        var current = await GetOperationAsync();

        var updated = await UpdateOperationAsync(current, UpdateFrom(current, enabled: false));

        Assert.False(updated.IsEnabled);
        Assert.NotEqual(current.RowVersion, updated.RowVersion);
        Assert.Equal(Admin, updated.UpdatedBy);
    }

    [Fact]
    public async Task Stale_update_is_a_409_conflict_and_does_not_overwrite_the_newer_change()
    {
        var original = await GetOperationAsync();

        // Admin A updates first.
        await UpdateOperationAsync(original, UpdateFrom(original, enabled: false));

        // Admin B still holds the old rowversion.
        var stale = UpdateFrom(original, enabled: true);
        stale.MaxRequestBytes = 1234;
        var ex = await Assert.ThrowsAsync<ProxyRejectedException>(() => UpdateOperationAsync(original, stale));

        Assert.Equal(ProxyErrorCode.ConfigurationConflict, ex.Code);
        Assert.Equal(409, ProxyErrors.StatusFor(ex.Code));

        var stored = Factory.WithDb(db => db.Operations.AsNoTracking().Single(o => o.Id == original.Id));
        Assert.False(stored.IsEnabled);                                  // A's change kept
        Assert.Equal(original.MaxRequestBytes, stored.MaxRequestBytes);  // B's change not applied
    }

    [Fact]
    public async Task Stale_destination_update_is_a_409_conflict()
    {
        var destination = await WithAdminAsync(async s => (await s.GetDestinationAsync(TestData.CustomersDestinationId, CancellationToken.None))!);
        var rowVersion = Convert.FromBase64String(destination.RowVersion);

        await WithAdminAsync(s => s.UpdateDestinationAsync(destination.Id, UpdateFrom(destination, false), rowVersion, Admin, CancellationToken.None));
        var ex = await Assert.ThrowsAsync<ProxyRejectedException>(() =>
            WithAdminAsync(s => s.UpdateDestinationAsync(destination.Id, UpdateFrom(destination, false), rowVersion, Admin, CancellationToken.None)));

        Assert.Equal(ProxyErrorCode.ConfigurationConflict, ex.Code);
    }

    [Fact]
    public async Task Disabling_a_destination_takes_effect_for_new_requests()
    {
        var destination = await WithAdminAsync(async s => (await s.GetDestinationAsync(TestData.CustomersDestinationId, CancellationToken.None))!);
        await WithAdminAsync(s => s.UpdateDestinationAsync(
            destination.Id, UpdateFrom(destination, false), Convert.FromBase64String(destination.RowVersion), Admin, CancellationToken.None));

        var proxied = await SendAsync(new { clientId = "customer.get", json = new { urlId = 5 } });

        await AssertProxyErrorAsync(proxied, 503, ProxyErrorCode.DestinationUnavailable);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("/customers/../admin/{urlId}")]
    [InlineData("https://evil.example/{urlId}")]
    [InlineData("customers/{urlId}")]
    [InlineData("/customers/{urlId}?admin=true")]
    public async Task Invalid_path_template_update_is_rejected(string template)
    {
        var original = await GetOperationAsync();
        var update = UpdateFrom(original, enabled: true);
        update.PathTemplate = template;

        var ex = await Assert.ThrowsAsync<ProxyRejectedException>(() => UpdateOperationAsync(original, update));

        Assert.Equal(ProxyErrorCode.InvalidParameters, ex.Code);
        Assert.Equal("/customers/{urlId}", Factory.WithDb(db => db.Operations.AsNoTracking().Single(o => o.Id == original.Id).PathTemplate));
    }
}
