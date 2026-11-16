namespace App.MinimalApi.Tests;

/// <summary>Required cases 11 and 12 (AES-256-GCM, AAD = clientId).</summary>
public sealed class EncryptionTests(ProxyWebAppFactory factory) : ProxyTestBase(factory), IClassFixture<ProxyWebAppFactory>
{
    private const string Operation = "customer.secure-get";

    private static EncryptedPayload Encrypt(string json, string aad = Operation, byte[]? key = null) =>
        AesGcmPayloadDecryptor.Encrypt(Encoding.UTF8.GetBytes(json), key ?? TestSecrets.EncryptionKey, aad);

    [Fact]
    public async Task Valid_encrypted_payload_is_decrypted_validated_and_forwarded()
    {
        var response = await SendAsync(new { clientId = Operation, encrypted = Encrypt("{\"urlId\":42}") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(Upstream.Requests);
        Assert.Equal("https://customers.internal:5001/customers/42", forwarded.Uri.AbsoluteUri);
    }

    // 11
    [Fact]
    public async Task Invalid_authentication_tag_is_rejected()
    {
        var payload = Encrypt("{\"urlId\":42}");
        var tag = Convert.FromBase64String(payload.Tag);
        tag[0] ^= 0x01;
        payload.Tag = Convert.ToBase64String(tag);

        var response = await SendAsync(new { clientId = Operation, encrypted = payload });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidEncryptedPayload);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        var payload = Encrypt("{\"urlId\":42}");
        var ciphertext = Convert.FromBase64String(payload.Ciphertext);
        ciphertext[^1] ^= 0x20;
        payload.Ciphertext = Convert.ToBase64String(ciphertext);

        var response = await SendAsync(new { clientId = Operation, encrypted = payload });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidEncryptedPayload);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Ciphertext_bound_to_another_operation_is_rejected()
    {
        // Encrypted correctly, but for a different clientId (AAD mismatch) - replay across operations.
        var response = await SendAsync(new { clientId = Operation, encrypted = Encrypt("{\"urlId\":42}", aad: "customer.get") });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidEncryptedPayload);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Payload_encrypted_with_another_key_is_rejected()
    {
        var response = await SendAsync(new { clientId = Operation, encrypted = Encrypt("{\"urlId\":42}", key: RandomNumberGenerator.GetBytes(32)) });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidEncryptedPayload);
        AssertNotForwarded();
    }

    // 12
    public static TheoryData<string, int, ProxyErrorCode> InvalidPayloads()
    {
        var valid = Encrypt("{\"urlId\":42}");
        string Env(string nonce, string ct, string tag) =>
            $"{{\"clientId\":\"{Operation}\",\"encrypted\":{{\"nonce\":\"{nonce}\",\"ciphertext\":\"{ct}\",\"tag\":\"{tag}\"}}}}";

        return new TheoryData<string, int, ProxyErrorCode>
        {
            // plaintext parameters sent to an operation that requires encryption
            { $"{{\"clientId\":\"{Operation}\",\"json\":{{\"urlId\":42}}}}", 400, ProxyErrorCode.EncryptionRequired },
            // both plaintext and encrypted
            { $"{{\"clientId\":\"{Operation}\",\"json\":{{\"urlId\":1}},\"encrypted\":{{\"nonce\":\"{valid.Nonce}\",\"ciphertext\":\"{valid.Ciphertext}\",\"tag\":\"{valid.Tag}\"}}}}", 400, ProxyErrorCode.InvalidEnvelope },
            { Env("not-base64!!", valid.Ciphertext, valid.Tag), 400, ProxyErrorCode.InvalidEncryptedPayload },
            { Env(Convert.ToBase64String(new byte[8]), valid.Ciphertext, valid.Tag), 400, ProxyErrorCode.InvalidEncryptedPayload }, // short nonce
            { Env(valid.Nonce, valid.Ciphertext, Convert.ToBase64String(new byte[12])), 400, ProxyErrorCode.InvalidEncryptedPayload }, // short tag
            { Env(valid.Nonce, "", valid.Tag), 400, ProxyErrorCode.InvalidEncryptedPayload },
            { $"{{\"clientId\":\"{Operation}\",\"encrypted\":{{\"nonce\":\"{valid.Nonce}\",\"ciphertext\":\"{valid.Ciphertext}\"}}}}", 400, ProxyErrorCode.InvalidEncryptedPayload }, // missing tag
            { $"{{\"clientId\":\"{Operation}\",\"encrypted\":\"{valid.Ciphertext}\"}}", 400, ProxyErrorCode.InvalidEncryptedPayload },
            { $"{{\"clientId\":\"{Operation}\",\"encrypted\":{{\"nonce\":\"{valid.Nonce}\",\"ciphertext\":\"{valid.Ciphertext}\",\"tag\":\"{valid.Tag}\",\"keyId\":\"other\"}}}}", 400, ProxyErrorCode.UnknownEnvelopeField }
        };
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task Invalid_encrypted_payloads_are_never_forwarded(string body, int status, ProxyErrorCode code)
    {
        var response = await SendRawAsync(body);

        await AssertProxyErrorAsync(response, status, code);
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("not json at all", ProxyErrorCode.InvalidEncryptedPayload)]
    [InlineData("[42]", ProxyErrorCode.InvalidEncryptedPayload)]
    [InlineData("{\"urlId\":\"../../admin\"}", ProxyErrorCode.InvalidParameters)]
    [InlineData("{\"urlId\":42,\"url\":\"https://evil.example\"}", ProxyErrorCode.InvalidParameters)]
    public async Task Authentic_but_invalid_decrypted_content_is_never_forwarded(string plaintext, ProxyErrorCode code)
    {
        var response = await SendAsync(new { clientId = Operation, encrypted = Encrypt(plaintext) });

        await AssertProxyErrorAsync(response, 400, code);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Encrypted_payload_sent_to_plaintext_operation_is_rejected()
    {
        var response = await SendAsync(new { clientId = "customer.get", encrypted = Encrypt("{\"urlId\":42}", aad: "customer.get") });

        await AssertProxyErrorAsync(response, 400, ProxyErrorCode.InvalidEnvelope);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Error_responses_do_not_reveal_crypto_details()
    {
        var payload = Encrypt("{\"urlId\":42}");
        payload.Tag = Convert.ToBase64String(new byte[16]);

        var body = await (await SendAsync(new { clientId = Operation, encrypted = payload })).Content.ReadAsStringAsync();

        Assert.DoesNotContain("tag", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AesGcm", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestSecrets.EncryptionKeyReference, body);
    }
}
