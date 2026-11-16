namespace App.MinimalApi.Webapi.Security;

/// <summary>
/// Authentication settings (configuration section <c>Security</c>).
/// Secret values (JWT signing key) must come from user-secrets / environment / a vault, never appsettings.json.
/// API keys are stored only as SHA-256 hashes.
/// </summary>
public sealed partial class SecuritySettings : IValidatableObject
{
    public const string SectionName = "Security";

    [Required]
    public JwtSettings Jwt { get; set; } = new();

    public List<ApiClientSettings> ApiClients { get; set; } = [];

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256HexRegex();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Jwt.Issuer)) yield return new("Security:Jwt:Issuer is required.");
        if (string.IsNullOrWhiteSpace(Jwt.Audience)) yield return new("Security:Jwt:Audience is required.");
        if (Jwt.GetSigningKeyBytes() is not { Length: >= 32 })
            yield return new("Security:Jwt:SigningKey must be a base64 value of at least 32 bytes (use user-secrets).");
        if (ApiClients.Count == 0) yield return new("Security:ApiClients must contain at least one client.");
        foreach (var client in ApiClients)
        {
            if (string.IsNullOrWhiteSpace(client.Name)) yield return new("Security:ApiClients:Name is required.");
            if (!Sha256HexRegex().IsMatch(client.ApiKeySha256 ?? string.Empty))
                yield return new($"Security:ApiClients '{client.Name}' ApiKeySha256 must be a 64 character hex SHA-256 hash.");
        }
    }
}

public sealed class JwtSettings
{
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>Base64 HMAC-SHA256 key (>= 32 bytes). Supplied by a secret store.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public byte[]? GetSigningKeyBytes()
    {
        try
        {
            return string.IsNullOrWhiteSpace(SigningKey) ? null : Convert.FromBase64String(SigningKey);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed class ApiClientSettings
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower/upper-case hex SHA-256 of the API key. The key itself is never stored.</summary>
    public string ApiKeySha256 { get; set; } = string.Empty;
}

public static class SecurityPolicies
{
    public const string ApiKeyScheme = "ApiKey";
    public const string JwtAuthenticationType = "Jwt";
    public const string ProxyClient = "ProxyClient";
}
