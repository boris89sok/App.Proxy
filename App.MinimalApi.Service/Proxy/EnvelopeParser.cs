namespace App.MinimalApi.Service.Proxy;

public sealed record ParsedEnvelope(
    string ClientId,
    IReadOnlyDictionary<string, JsonElement>? Parameters,
    EncryptedPayload? Encrypted,
    int BodyLength);

/// <summary>
/// Strict parser of the <c>/api/proxy/execute</c> envelope.
/// - Only <c>clientId</c>, <c>json</c> and <c>encrypted</c> are accepted (exact, case-sensitive names).
///   Anything else (url, method, path, destination, headers, ...) is rejected, so trusted
///   configuration can never be overridden by the caller.
/// - Duplicate property names are rejected (System.Text.Json would silently keep the last one,
///   which enables parameter smuggling between validators and consumers).
/// </summary>
public static partial class EnvelopeParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNameRegex();

    public static ParsedEnvelope Parse(ReadOnlyMemory<byte> body)
    {
        using var document = ParseDocument(body, ProxyErrorCode.InvalidEnvelope, "MalformedJson");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEnvelope, "EnvelopeNotObject");
        }

        string? clientId = null;
        IReadOnlyDictionary<string, JsonElement>? parameters = null;
        EncryptedPayload? encrypted = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new ProxyRejectedException(ProxyErrorCode.InvalidEnvelope, "DuplicateEnvelopeField");
            }

            switch (property.Name)
            {
                case "clientId":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new ProxyRejectedException(ProxyErrorCode.InvalidEnvelope, "ClientIdNotString");
                    }
                    clientId = property.Value.GetString();
                    break;

                case "json":
                    parameters = ReadParameterObject(property.Value);
                    break;

                case "encrypted":
                    encrypted = ReadEncrypted(property.Value);
                    break;

                default:
                    throw new ProxyRejectedException(
                        ProxyErrorCode.UnknownEnvelopeField,
                        "UnknownEnvelopeField:" + SafeName(property.Name));
            }
        }

        if (string.IsNullOrEmpty(clientId) || !OperationKeyRegex().IsMatch(clientId))
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEnvelope, "InvalidClientId");
        }

        if ((parameters is null) == (encrypted is null))
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEnvelope, "ExactlyOneOfJsonOrEncryptedRequired");
        }

        return new ParsedEnvelope(clientId, parameters, encrypted, body.Length);
    }

    /// <summary>Parses decrypted plaintext; it must be a JSON object of parameters.</summary>
    public static IReadOnlyDictionary<string, JsonElement> ParseParameters(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = ParseDocument(utf8Json, ProxyErrorCode.InvalidEncryptedPayload, "DecryptedPayloadNotJson");
        return ReadParameterObject(document.RootElement, ProxyErrorCode.InvalidEncryptedPayload);
    }

    /// <summary>Returns the name if it is safe to write into the audit log, otherwise a placeholder.</summary>
    public static string SafeName(string name) => SafeNameRegex().IsMatch(name) ? name : "<invalid>";

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> body, ProxyErrorCode code, string reason)
    {
        try
        {
            return JsonDocument.Parse(body, DocumentOptions);
        }
        catch (JsonException)
        {
            throw new ProxyRejectedException(code, reason);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadParameterObject(
        JsonElement element,
        ProxyErrorCode code = ProxyErrorCode.InvalidEnvelope)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ProxyRejectedException(code, "ParametersNotObject");
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new ProxyRejectedException(code, "DuplicateParameter");
            }
        }

        return result;
    }

    private static EncryptedPayload ReadEncrypted(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEncryptedPayload, "EncryptedNotObject");
        }

        var payload = new EncryptedPayload();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ProxyRejectedException(ProxyErrorCode.InvalidEncryptedPayload, "EncryptedFieldInvalid");
            }

            switch (property.Name)
            {
                case "nonce": payload.Nonce = property.Value.GetString()!; break;
                case "ciphertext": payload.Ciphertext = property.Value.GetString()!; break;
                case "tag": payload.Tag = property.Value.GetString()!; break;
                default:
                    throw new ProxyRejectedException(ProxyErrorCode.UnknownEnvelopeField, "UnknownEncryptedField:" + SafeName(property.Name));
            }
        }

        if (seen.Count != 3)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEncryptedPayload, "EncryptedFieldMissing");
        }

        return payload;
    }
}
