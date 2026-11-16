namespace App.MinimalApi.Tests.Infrastructure;

/// <summary>Trusted configuration used by the tests (mirrors the shape of Database/*.sql seed data).</summary>
public static class TestData
{
    public const string CustomersBaseUrl = "https://customers.internal:5001";

    public const int CustomersDestinationId = 1;
    public const int DisabledDestinationId = 2;
    public const int SlowDestinationId = 3;

    public const int CustomerGetOperationId = 1;

    public static void Seed(SecureProxyContext db)
    {
        var now = DateTime.UtcNow;

        var customers = new Destination { Id = CustomersDestinationId, Name = "CustomersApi", BaseUrl = CustomersBaseUrl, IsEnabled = true, TimeoutSeconds = 10, AllowedPorts = "5001", CreatedAt = now, UpdatedAt = now };
        var disabled = new Destination { Id = DisabledDestinationId, Name = "LegacyApi", BaseUrl = "https://legacy.internal", IsEnabled = false, TimeoutSeconds = 10, CreatedAt = now, UpdatedAt = now };
        var slow = new Destination { Id = SlowDestinationId, Name = "SlowApi", BaseUrl = "https://slow.internal/api", IsEnabled = true, TimeoutSeconds = 1, CreatedAt = now, UpdatedAt = now };
        db.Destinations.AddRange(customers, disabled, slow);

        var profile = new EncryptionProfile { Id = 1, Name = "test-aes", Algorithm = "AES-256-GCM", KeyReference = TestSecrets.EncryptionKeyReference, KeyVersion = 1, IsEnabled = true, CreatedAt = now, UpdatedAt = now };
        db.EncryptionProfiles.Add(profile);

        var ruleId = 1;
        ParameterRule IntUrlId() => new() { Id = ruleId++, Name = "urlId", IsRequired = true, DataType = "int", MinValue = 1, MaxValue = int.MaxValue, Location = "Path" };

        // 1. customer.get - the operation from 001_Initial.sql (+ header rules from 002)
        var customerGet = Op(CustomerGetOperationId, "customer.get", customers, "/customers/{urlId}");
        customerGet.ParameterRules.Add(IntUrlId());
        customerGet.HeaderRules.Add(new HeaderRule { Id = 1, HeaderName = "X-Request-Source", MaxLength = 50, RegexPattern = "[A-Za-z0-9\\-]+" });
        // Misconfiguration on purpose: sensitive headers in the allowlist must STILL never be forwarded.
        customerGet.HeaderRules.Add(new HeaderRule { Id = 2, HeaderName = "Authorization" });
        customerGet.HeaderRules.Add(new HeaderRule { Id = 3, HeaderName = "Cookie" });

        // 2. encrypted operation
        var secureGet = Op(2, "customer.secure-get", customers, "/customers/{urlId}");
        secureGet.RequiresEncryption = true;
        secureGet.EncryptionProfile = profile;
        secureGet.ParameterRules.Add(IntUrlId());

        // 3. query parameters + unknown parameters allowed (and dropped)
        var search = Op(3, "customer.search", customers, "/customers/{urlId}/orders");
        search.AllowUnknownParameters = true;
        search.ParameterRules.Add(IntUrlId());
        search.ParameterRules.Add(new ParameterRule { Id = ruleId++, Name = "status", IsRequired = true, DataType = "string", MaxLength = 20, RegexPattern = "open|closed", Location = "Query" });
        search.ParameterRules.Add(new ParameterRule { Id = ruleId++, Name = "from", IsRequired = false, DataType = "date", Location = "Query" });

        // 4. disabled operation
        var disabledOp = Op(4, "customer.delete-all", customers, "/customers/{urlId}");
        disabledOp.IsEnabled = false;
        disabledOp.ParameterRules.Add(IntUrlId());

        // 5. operation on a disabled destination
        var legacy = Op(5, "legacy.get", disabled, "/items/{urlId}");
        legacy.ParameterRules.Add(IntUrlId());

        // 6. string path parameter (path traversal target)
        var files = Op(6, "files.get", customers, "/files/{urlId}");
        files.ParameterRules.Add(new ParameterRule { Id = ruleId++, Name = "urlId", IsRequired = true, DataType = "string", MaxLength = 100, Location = "Path" });

        // 7. method that the implementation does not allow (e.g. DB constraint relaxed/bypassed)
        var badMethod = Op(7, "customer.purge", customers, "/customers/{urlId}");
        badMethod.HttpMethod = "DELETE";
        badMethod.ParameterRules.Add(IntUrlId());

        // 8. slow destination with a 1 second timeout
        var slowOp = Op(8, "slow.get", slow, "/reports/{urlId}");
        slowOp.ParameterRules.Add(IntUrlId());

        // 9. tight response limit
        var small = Op(9, "customer.small", customers, "/customers/{urlId}");
        small.MaxResponseBytes = 64;
        small.ParameterRules.Add(IntUrlId());

        db.Operations.AddRange(customerGet, secureGet, search, disabledOp, legacy, files, badMethod, slowOp, small);
        db.SaveChanges();
    }

    private static Operation Op(int id, string key, Destination destination, string path)
    {
        var now = DateTime.UtcNow;
        return new Operation
        {
            Id = id,
            OperationKey = key,
            Destination = destination,
            HttpMethod = "GET",
            PathTemplate = path,
            IsEnabled = true,
            MaxRequestBytes = 65536,
            MaxResponseBytes = 1_048_576,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
