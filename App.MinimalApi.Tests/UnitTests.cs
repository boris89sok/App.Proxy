namespace App.MinimalApi.Tests;

public sealed class UpstreamRequestBuilderTests
{
    private static OperationSnapshot Op(string template, string baseUrl = "https://api.internal/base", string? ports = null) => new(
        1, "op", "GET", template, true, 1000, 1000, false, false, null,
        new DestinationSnapshot(1, "d", baseUrl, true, 10, ports),
        [new ParameterRuleSnapshot("urlId", true, "string", null, 100, null, null, null, "Path")], []);

    private static ValidatedParameters Path(string value) => new(new Dictionary<string, string> { ["urlId"] = value }, []);

    [Theory]
    [InlineData("/customers/{urlId}", true)]
    [InlineData("/customers/{urlId}/orders", true)]
    [InlineData("/v1.2/items_{urlId}", true)]
    [InlineData("customers/{urlId}", false)]
    [InlineData("/customers/../{urlId}", false)]
    [InlineData("/customers/./{urlId}", false)]
    [InlineData("//evil.example/{urlId}", false)]
    [InlineData("/customers/{urlId}?x=1", false)]
    [InlineData("/customers\\{urlId}", false)]
    [InlineData("/customers/%2e%2e/{urlId}", false)]
    [InlineData("", false)]
    public void Path_template_validation(string template, bool valid) =>
        Assert.Equal(valid, UpstreamRequestBuilder.IsValidPathTemplate(template));

    [Fact]
    public void Base_path_of_the_destination_is_preserved()
    {
        var uri = UpstreamRequestBuilder.BuildUri(Op("/customers/{urlId}"), Path("7"), allowHttp: false);
        Assert.Equal("https://api.internal/base/customers/7", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://api.internal")]          // http not allowed
    [InlineData("https://user:pw@api.internal")] // credentials in URL
    [InlineData("https://api.internal?x=1")]     // query in base
    [InlineData("ftp://api.internal")]
    [InlineData("not a url")]
    public void Invalid_destination_base_url_fails_closed(string baseUrl)
    {
        var ex = Assert.Throws<ProxyRejectedException>(() => UpstreamRequestBuilder.BuildUri(Op("/c/{urlId}", baseUrl), Path("1"), false));
        Assert.Equal(ProxyErrorCode.OperationMisconfigured, ex.Code);
    }

    [Theory]
    [InlineData("https://api.internal:8443", null, false)] // non-default port without allowlist
    [InlineData("https://api.internal:8443", "443", false)]
    [InlineData("https://api.internal:8443", "8443", true)]
    [InlineData("https://api.internal", null, true)]
    [InlineData("https://api.internal", "abc", false)]
    public void Ports_are_restricted_to_the_allowlist(string baseUrl, string? ports, bool allowed)
    {
        var build = () => UpstreamRequestBuilder.BuildUri(Op("/c/{urlId}", baseUrl, ports), Path("1"), false);
        if (allowed) build();
        else Assert.Equal(ProxyErrorCode.OperationMisconfigured, Assert.Throws<ProxyRejectedException>(build).Code);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("cookie")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("Proxy-Anything")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Host")]
    public void Sensitive_and_hop_by_hop_headers_are_always_denied(string header) =>
        Assert.True(UpstreamRequestBuilder.IsDeniedHeader(header));
}

public sealed class ParameterValidatorTests
{
    private static OperationSnapshot Op(params ParameterRuleSnapshot[] rules) => new(
        1, "op", "GET", "/x/{urlId}", true, 1000, 1000, false, false, null, null, rules, []);

    private static IReadOnlyDictionary<string, JsonElement> Input(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Theory]
    [InlineData("{\"urlId\":\"+0005\"}", "5")]
    [InlineData("{\"urlId\":5}", "5")]
    [InlineData("{\"urlId\":\"-0\"}", "0")]
    public void Int_values_are_canonicalised(string json, string expected)
    {
        var result = ParameterValidator.Validate(Op(new ParameterRuleSnapshot("urlId", true, "int", null, null, null, null, null, "Path")), Input(json));
        Assert.Equal(expected, result.Path["urlId"]);
    }

    [Fact]
    public void Guid_values_are_canonicalised()
    {
        var result = ParameterValidator.Validate(Op(new ParameterRuleSnapshot("urlId", true, "guid", null, null, null, null, null, "Path")),
            Input("{\"urlId\":\"{6F9619FF-8B86-D011-B42D-00C04FC964FF}\"}"));
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", result.Path["urlId"]);
    }

    [Fact]
    public void Regex_is_a_full_match()
    {
        var op = Op(new ParameterRuleSnapshot("urlId", true, "string", null, null, null, null, "[a-z]+", "Path"));
        Assert.Throws<ProxyRejectedException>(() => ParameterValidator.Validate(op, Input("{\"urlId\":\"abc1\"}")));
        Assert.Equal("abc", ParameterValidator.Validate(op, Input("{\"urlId\":\"abc\"}")).Path["urlId"]);
    }

    [Fact]
    public void Catastrophic_regex_times_out_and_is_rejected()
    {
        var op = Op(new ParameterRuleSnapshot("urlId", true, "string", null, 5000, null, null, "(a+)+$", "Path"));
        var input = Input($"{{\"urlId\":\"{new string('a', 4000)}!\"}}");
        var ex = Assert.Throws<ProxyRejectedException>(() => ParameterValidator.Validate(op, input));
        Assert.Equal(ProxyErrorCode.InvalidParameters, ex.Code);
    }

    [Fact]
    public void Invalid_configured_regex_fails_closed()
    {
        var op = Op(new ParameterRuleSnapshot("urlId", true, "string", null, null, null, null, "([", "Path"));
        Assert.Equal(ProxyErrorCode.OperationMisconfigured,
            Assert.Throws<ProxyRejectedException>(() => ParameterValidator.Validate(op, Input("{\"urlId\":\"a\"}"))).Code);
    }

    [Fact]
    public void Unknown_data_type_fails_closed()
    {
        var op = Op(new ParameterRuleSnapshot("urlId", true, "decimal", null, null, null, null, null, "Path"));
        Assert.Equal(ProxyErrorCode.OperationMisconfigured,
            Assert.Throws<ProxyRejectedException>(() => ParameterValidator.Validate(op, Input("{\"urlId\":1}"))).Code);
    }
}

public sealed class ProxyConfigCacheTests
{
    private sealed class CountingRepository : IOperationRepository
    {
        public int Loads;
        public bool Enabled = true;

        public Task<Operation?> GetByKeyWithGraphAsync(string operationKey, CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult<Operation?>(operationKey != "op" ? null : new Operation
            {
                Id = 1, OperationKey = "op", HttpMethod = "GET", PathTemplate = "/x/{urlId}", IsEnabled = Enabled,
                Destination = new Destination { Id = 1, Name = "d", BaseUrl = "https://d", IsEnabled = true, TimeoutSeconds = 5 }
            });
        }

        // Members not used by the cache
        public Task<IReadOnlyList<string>> GetKeysByDestinationAsync(int destinationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SetExpectedRowVersion(Operation operation, byte[] rowVersion) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IEnumerable<Operation>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Operation?> GetByIdAsync(object id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Operation> GetAsync(System.Linq.Expressions.Expression<Func<Operation, bool>> where, string[] include = null!) => throw new NotSupportedException();
        public Task<Operation> GetNoTrackingAsync(System.Linq.Expressions.Expression<Func<Operation, bool>> where, string[] include = null!) => throw new NotSupportedException();
        public Task<IEnumerable<Operation>> GetManyAsync(System.Linq.Expressions.Expression<Func<Operation, bool>> where, string[] include = null!) => throw new NotSupportedException();
        public Task<IEnumerable<Operation>> GetManyNoTrackingAsync(System.Linq.Expressions.Expression<Func<Operation, bool>> where, string[] include = null!) => throw new NotSupportedException();
        public Task<Operation> AddAsync(Operation entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Operation> AddDetachedAsync(Operation entity) => throw new NotSupportedException();
        public Task AddRangeAsync(List<Operation> entities) => throw new NotSupportedException();
        public Task UpdateAsync(Operation entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateDetachedAsync(Operation entity) => throw new NotSupportedException();
        public Task<Operation> UpdateAsync(Operation entity, string[] excluded) => throw new NotSupportedException();
        public Task UpdateRangeAsync(List<Operation> entities) => throw new NotSupportedException();
        public Task DeleteAsync(object id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveRangeAsync(System.Linq.Expressions.Expression<Func<Operation, bool>> where) => throw new NotSupportedException();
        public IQueryable<Operation> GetAll() => throw new NotSupportedException();
        public Operation GetById(object id) => throw new NotSupportedException();
        public Operation Get(System.Linq.Expressions.Expression<Func<Operation, bool>> where) => throw new NotSupportedException();
        public Operation GetNoTracking(System.Linq.Expressions.Expression<Func<Operation, bool>> where) => throw new NotSupportedException();
        public IQueryable<Operation> GetMany(System.Linq.Expressions.Expression<Func<Operation, bool>> where) => throw new NotSupportedException();
        public IQueryable<Operation> GetManyNoTracking(System.Linq.Expressions.Expression<Func<Operation, bool>> where) => throw new NotSupportedException();
        public Operation Add(Operation entity) => throw new NotSupportedException();
        public Operation AddDetached(Operation entity) => throw new NotSupportedException();
        public Operation Update(Operation entity) => throw new NotSupportedException();
        public Operation Update(Operation entity, string[] excluded) => throw new NotSupportedException();
        public void Delete(object id) => throw new NotSupportedException();
        public void RemoveRange(System.Linq.Expressions.Expression<Func<Operation, bool>> where) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Snapshot_is_cached_until_invalidated()
    {
        var repository = new CountingRepository();
        var cache = new ProxyConfigCache(new MemoryCache(new MemoryCacheOptions()), repository,
            Options.Create(new EnvOptions { ConfigCacheSeconds = 60, NegativeCacheSeconds = 60 }));

        Assert.True((await cache.GetOperationAsync("op", default))!.IsEnabled);
        repository.Enabled = false;
        Assert.True((await cache.GetOperationAsync("op", default))!.IsEnabled); // still cached (documented staleness <= TTL)
        Assert.Equal(1, repository.Loads);

        cache.Invalidate("op");
        Assert.False((await cache.GetOperationAsync("op", default))!.IsEnabled);
        Assert.Equal(2, repository.Loads);
    }

    [Fact]
    public async Task Unknown_operations_are_negatively_cached()
    {
        var repository = new CountingRepository();
        var cache = new ProxyConfigCache(new MemoryCache(new MemoryCacheOptions()), repository,
            Options.Create(new EnvOptions { ConfigCacheSeconds = 60, NegativeCacheSeconds = 60 }));

        Assert.Null(await cache.GetOperationAsync("nope", default));
        Assert.Null(await cache.GetOperationAsync("nope", default));
        Assert.Equal(1, repository.Loads);
    }
}
