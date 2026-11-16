namespace App.MinimalApi.Service.Proxy;

/// <summary>
/// Secure key source abstraction. Implementations resolve a key REFERENCE stored in the database
/// (EncryptionProfiles.KeyReference + KeyVersion) to key material held outside the database
/// (user-secrets / environment for local runs, Azure Key Vault / HSM in production).
/// </summary>
public interface ISecretKeyProvider
{
    /// <summary>Returns a fresh copy of the key (caller zeroes it) or null when it is not available.</summary>
    byte[]? GetKey(string keyReference, int keyVersion);
}

/// <summary>
/// Reads keys from <c>IConfiguration</c> at <c>Proxy:EncryptionKeys:{reference}:v{version}</c> (base64, 32 bytes).
/// Only configuration providers that are secret stores should hold this section (user-secrets, environment
/// variables, Key Vault configuration provider) - never appsettings.json.
/// </summary>
public sealed partial class ConfigurationSecretKeyProvider(IConfiguration configuration) : ISecretKeyProvider
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    public byte[]? GetKey(string keyReference, int keyVersion)
    {
        // The reference is used as a configuration path segment: never allow ':' or other separators.
        if (!ReferenceRegex().IsMatch(keyReference) || keyVersion < 1)
        {
            return null;
        }

        var value = configuration[$"Proxy:EncryptionKeys:{keyReference}:v{keyVersion}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var buffer = new byte[64];
        if (!Convert.TryFromBase64String(value.Trim(), buffer, out var written))
        {
            CryptographicOperations.ZeroMemory(buffer);
            return null;
        }

        var key = buffer[..written];
        CryptographicOperations.ZeroMemory(buffer);
        return key;
    }
}

public interface IPayloadDecryptor
{
    /// <summary>Authenticates and decrypts an AES-256-GCM payload bound to <paramref name="associatedData"/>.</summary>
    /// <exception cref="ProxyRejectedException">The payload is malformed, tampered with, or the key is unavailable.</exception>
    byte[] Decrypt(EncryptedPayload payload, EncryptionProfileSnapshot profile, string associatedData, int maxPlaintextBytes);
}

/// <summary>
/// AES-256-GCM with a 12 byte nonce and 16 byte tag. The associated data is the operation key
/// (<c>clientId</c>), so a ciphertext produced for one operation cannot be replayed against another.
/// Error responses never reveal which check failed.
/// </summary>
public sealed class AesGcmPayloadDecryptor(ISecretKeyProvider keyProvider) : IPayloadDecryptor
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public byte[] Decrypt(EncryptedPayload payload, EncryptionProfileSnapshot profile, string associatedData, int maxPlaintextBytes)
    {
        if (!profile.IsEnabled || !string.Equals(profile.Algorithm, "AES-256-GCM", StringComparison.Ordinal))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "EncryptionProfileUnavailable");
        }

        var nonce = DecodeExact(payload.Nonce, NonceSize);
        var tag = DecodeExact(payload.Tag, TagSize);
        var ciphertext = Decode(payload.Ciphertext, maxPlaintextBytes);

        var key = keyProvider.GetKey(profile.KeyReference, profile.KeyVersion);
        if (key is null || key.Length != KeySize)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "EncryptionKeyUnavailable");
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(associatedData));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEncryptedPayload, "AuthenticationFailed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DecodeExact(string value, int size)
    {
        var bytes = Decode(value, size);
        if (bytes.Length != size)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEncryptedPayload, "EncryptedFieldLength");
        }
        return bytes;
    }

    private static byte[] Decode(string value, int maxBytes)
    {
        // Bound the work before decoding: base64 expands 3 bytes into 4 chars.
        if (string.IsNullOrEmpty(value) || value.Length > ((maxBytes + 2) / 3) * 4)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEncryptedPayload, "EncryptedFieldLength");
        }

        var buffer = new byte[(value.Length / 4 + 1) * 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written) || written == 0)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEncryptedPayload, "EncryptedFieldNotBase64");
        }
        return buffer[..written];
    }

    /// <summary>Helper for clients/tests: encrypts a parameters object for an operation.</summary>
    public static EncryptedPayload Encrypt(ReadOnlySpan<byte> plaintext, byte[] key, string associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(associatedData));
        return new EncryptedPayload
        {
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Tag = Convert.ToBase64String(tag)
        };
    }
}
