namespace App.MinimalApi.Tests.Infrastructure;

/// <summary>Test-only secrets. Generated per test run, never written to disk.</summary>
public static class TestSecrets
{
    public const string Issuer = "https://auth.test";
    public const string Audience = "secure-proxy-tests";
    public const string ApiClientName = "test-client";
    public const string EncryptionKeyReference = "test-aes";

    public static readonly string ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);
    public static readonly byte[] EncryptionKey = RandomNumberGenerator.GetBytes(32);

    public static string ApiKeyHash => App.MinimalApi.Webapi.Security.ApiKeyAuthenticationHandler.HashKey(ApiKey);

    public static string CreateToken(string subject = "alice", TimeSpan? lifetime = null, byte[]? signingKey = null)
    {
        var claims = new List<Claim> { new("sub", subject) };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)).AddMinutes(-15),
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(signingKey ?? SigningKey), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
