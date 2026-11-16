namespace App.MinimalApi.Service.Proxy;

public interface IProxyExecutionService
{
    /// <summary>Executes <c>POST /api/proxy/execute</c> and writes the response to <paramref name="context"/>.</summary>
    Task ExecuteAsync(HttpContext context);
}

/// <summary>
/// Fail-closed proxy pipeline. Steps 1-6 only read trusted configuration and validate caller input;
/// the upstream is contacted in step 8 only after every step succeeded.
/// <list type="number">
/// <item>Envelope: content type, bounded body read, strict JSON shape.</item>
/// <item>Operation lookup by clientId; operation / destination enabled; method allowlist.</item>
/// <item>Size limit of the operation.</item>
/// <item>Decryption (AES-256-GCM) when the operation requires it.</item>
/// <item>Parameter validation and canonicalisation.</item>
/// <item>Upstream URI + header allowlist built from configuration.</item>
/// <item>Audit pre-write (Mandatory mode only).</item>
/// <item>Forward with timeout, caller cancellation, bounded streaming of the response.</item>
/// </list>
/// </summary>
public sealed class ProxyExecutionService(
    IProxyConfigCache configCache,
    IPayloadDecryptor decryptor,
    IRequestAuditor auditor,
    IHttpClientFactory httpClientFactory,
    IOptions<EnvOptions> options,
    TimeProvider timeProvider,
    ILogger<ProxyExecutionService> logger) : IProxyExecutionService
{
    /// <summary>Methods the proxy implementation is allowed to forward (stage 1: GET only, matches CK_Operations_Method).</summary>
    private static readonly HashSet<string> SupportedMethods = new(StringComparer.Ordinal) { "GET" };

    public async Task ExecuteAsync(HttpContext context)
    {
        var aborted = context.RequestAborted;
        var started = timeProvider.GetTimestamp();
        var audit = new AuditRecord
        {
            CorrelationId = context.TraceIdentifier,
            StartedAt = timeProvider.GetUtcNow().UtcDateTime
        };

        try
        {
            // 1. Envelope
            var envelope = await ReadEnvelopeAsync(context, aborted);

            // 2. Trusted configuration
            var operation = await configCache.GetOperationAsync(envelope.ClientId, aborted)
                ?? throw new ProxyRejectedException(ProxyErrorCode.UnknownOperation, "UnknownOperation");
            audit.OperationKey = operation.Key;
            audit.DestinationId = operation.Destination?.Id;
            EnsureExecutable(operation);

            // 3. Operation size limit
            if (envelope.BodyLength > operation.MaxRequestBytes)
            {
                throw new ProxyRejectedException(ProxyErrorCode.RequestTooLarge, "OperationMaxRequestBytesExceeded");
            }

            // 4. Decryption
            var parameters = ResolveParameters(operation, envelope);

            // 5. Parameters
            var validated = ParameterValidator.Validate(operation, parameters);

            // 6. Upstream request from configuration only
            var uri = UpstreamRequestBuilder.BuildUri(operation, validated, options.Value.AllowHttpUpstream);
            var headers = UpstreamRequestBuilder.SelectHeaders(operation, context.Request.Headers);

            // 7. Audit pre-write
            if (!await auditor.TryBeginForwardAsync(audit, aborted))
            {
                throw new ProxyRejectedException(ProxyErrorCode.AuditUnavailable, "AuditUnavailable", outcome: AuditOutcome.Failed);
            }

            // 8. Forward
            await ForwardAsync(context, operation, uri, headers, audit);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            audit.Outcome = AuditOutcome.ClientCancelled;
            audit.ProxyStatus = ProxyErrors.ClientClosedRequest;
            audit.Reason = "CallerCancelled";
        }
        catch (ProxyRejectedException ex)
        {
            audit.Outcome = ex.Outcome;
            audit.Reason = ex.AuditReason;
            audit.ProxyStatus = ProxyErrors.ResponseStatus(ex.Code, ex.UpstreamStatus);
            audit.UpstreamStatus ??= ex.UpstreamStatus;
            await ProxyErrors.WriteAsync(context, ex.Code, ex.Errors, ex.UpstreamStatus, aborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected proxy failure {CorrelationId}", audit.CorrelationId);
            audit.Outcome = AuditOutcome.Failed;
            audit.Reason = "UnexpectedError:" + ex.GetType().Name;
            audit.ProxyStatus = StatusCodes.Status500InternalServerError;
            await ProxyErrors.WriteAsync(context, ProxyErrorCode.InternalError, cancellationToken: aborted);
        }
        finally
        {
            audit.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            audit.DurationMs = (int)Math.Min(int.MaxValue, timeProvider.GetElapsedTime(started).TotalMilliseconds);

            logger.LogInformation(
                "Proxy execution {Outcome} {OperationKey} {ProxyStatus} {UpstreamStatus} {Forwarded} {DurationMs}ms {Reason}",
                audit.Outcome, audit.OperationKey, audit.ProxyStatus, audit.UpstreamStatus, audit.Forwarded, audit.DurationMs, audit.Reason);

            await auditor.CompleteAsync(audit);
        }
    }

    private async Task<ParsedEnvelope> ReadEnvelopeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || (mediaType.Charset.HasValue && !mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProxyRejectedException(ProxyErrorCode.UnsupportedMediaType, "UnsupportedMediaType");
        }

        var cap = options.Value.GlobalMaxRequestBytes;
        if (request.ContentLength > cap)
        {
            throw new ProxyRejectedException(ProxyErrorCode.RequestTooLarge, "GlobalMaxRequestBytesExceeded");
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = cap;
        }

        // Bounded read: never buffers more than cap + one chunk.
        var buffer = new ArrayBufferWriter<byte>(Math.Min(cap, 16 * 1024));
        try
        {
            while (true)
            {
                var memory = buffer.GetMemory(4096);
                var read = await request.Body.ReadAsync(memory, cancellationToken);
                if (read == 0) break;
                buffer.Advance(read);
                if (buffer.WrittenCount > cap)
                {
                    throw new ProxyRejectedException(ProxyErrorCode.RequestTooLarge, "GlobalMaxRequestBytesExceeded");
                }
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new ProxyRejectedException(ProxyErrorCode.RequestTooLarge, "GlobalMaxRequestBytesExceeded");
        }

        if (buffer.WrittenCount == 0)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidEnvelope, "EmptyBody");
        }

        return EnvelopeParser.Parse(buffer.WrittenMemory);
    }

    private static void EnsureExecutable(OperationSnapshot operation)
    {
        if (!operation.IsEnabled)
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationDisabled, "OperationDisabled");
        }
        if (operation.Destination is null)
        {
            throw new ProxyRejectedException(ProxyErrorCode.DestinationUnavailable, "DestinationMissing");
        }
        if (!operation.Destination.IsEnabled)
        {
            throw new ProxyRejectedException(ProxyErrorCode.DestinationUnavailable, "DestinationDisabled");
        }
        // Fail closed even if the database constraint is bypassed or relaxed.
        if (!SupportedMethods.Contains(operation.HttpMethod))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "MethodNotAllowed");
        }
        if (!ProxyLimits.IsValidTimeout(operation.Destination.TimeoutSeconds))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "InvalidTimeout");
        }
    }

    private IReadOnlyDictionary<string, JsonElement> ResolveParameters(OperationSnapshot operation, ParsedEnvelope envelope)
    {
        if (!operation.RequiresEncryption)
        {
            return envelope.Parameters
                ?? throw new ProxyRejectedException(ProxyErrorCode.InvalidEnvelope, "EncryptedPayloadNotExpected");
        }

        if (envelope.Encrypted is null)
        {
            throw new ProxyRejectedException(ProxyErrorCode.EncryptionRequired, "PlaintextForEncryptedOperation");
        }

        var profile = operation.EncryptionProfile
            ?? throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "EncryptionProfileMissing");

        var plaintext = decryptor.Decrypt(envelope.Encrypted, profile, operation.Key, operation.MaxRequestBytes);
        try
        {
            return EnvelopeParser.ParseParameters(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task ForwardAsync(
        HttpContext context,
        OperationSnapshot operation,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        AuditRecord audit)
    {
        var aborted = context.RequestAborted;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(operation.Destination!.TimeoutSeconds), timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(aborted, timeoutCts.Token);
        var token = linkedCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.TryAddWithoutValidation(ProxyHeaders.CorrelationId, audit.CorrelationId);

        var client = httpClientFactory.CreateClient(ProxyServiceCollectionExtensions.HttpClientName);
        HttpResponseMessage response;
        audit.Forwarded = true;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new ProxyRejectedException(ProxyErrorCode.UpstreamTimeout, "UpstreamTimeout", outcome: AuditOutcome.Timeout);
        }
        catch (BrokenCircuitException)
        {
            audit.Forwarded = false; // short-circuited before reaching the network
            throw new ProxyRejectedException(ProxyErrorCode.DestinationUnavailable, "CircuitOpen", outcome: AuditOutcome.Failed);
        }
        catch (HttpRequestException ex)
        {
            throw new ProxyRejectedException(ProxyErrorCode.UpstreamUnavailable, "UpstreamConnectionFailure:" + ex.HttpRequestError,
                outcome: AuditOutcome.UpstreamError);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            audit.UpstreamStatus = status;

            if (status >= 500)
            {
                // Preserve the upstream status, but never relay upstream 5xx bodies (stack traces, internal hostnames).
                throw new ProxyRejectedException(ProxyErrorCode.UpstreamError, "UpstreamServerError",
                    outcome: AuditOutcome.UpstreamError, upstreamStatus: status);
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > operation.MaxResponseBytes)
            {
                throw new ProxyRejectedException(ProxyErrorCode.UpstreamResponseTooLarge, "UpstreamResponseTooLarge",
                    outcome: AuditOutcome.UpstreamError, upstreamStatus: status);
            }

            context.Response.StatusCode = status;
            CopyResponseHeaders(response, context.Response);
            context.Response.Headers[ProxyHeaders.Source] = ProxyHeaders.SourceUpstream;
            audit.ProxyStatus = status;
            audit.Outcome = status < 400 ? AuditOutcome.Success : AuditOutcome.UpstreamError;
            audit.Reason = status < 400 ? null : "UpstreamClientError";

            try
            {
                await using var upstreamBody = await response.Content.ReadAsStreamAsync(token);
                await CopyBoundedAsync(upstreamBody, context, operation.MaxResponseBytes, audit, token);
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.Headers.Clear();
                    throw new ProxyRejectedException(ProxyErrorCode.UpstreamTimeout, "UpstreamTimeoutDuringBody",
                        outcome: AuditOutcome.Timeout, upstreamStatus: status);
                }
                audit.Outcome = AuditOutcome.Timeout;
                audit.Reason = "UpstreamTimeoutDuringBody";
                context.Abort();
            }
            catch (HttpRequestException) when (!aborted.IsCancellationRequested)
            {
                audit.Outcome = AuditOutcome.UpstreamError;
                audit.Reason = "UpstreamBodyReadFailure";
                if (!context.Response.HasStarted)
                {
                    context.Response.Headers.Clear();
                    throw new ProxyRejectedException(ProxyErrorCode.UpstreamUnavailable, "UpstreamBodyReadFailure",
                        outcome: AuditOutcome.UpstreamError, upstreamStatus: status);
                }
                context.Abort();
            }
        }
    }

    /// <summary>Streams the upstream body to the caller, aborting when the configured limit is exceeded.</summary>
    private static async Task CopyBoundedAsync(Stream source, HttpContext context, int maxBytes, AuditRecord audit, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    audit.Outcome = AuditOutcome.UpstreamError;
                    audit.Reason = "UpstreamResponseTooLarge";
                    if (!context.Response.HasStarted)
                    {
                        context.Response.Headers.Clear();
                        throw new ProxyRejectedException(ProxyErrorCode.UpstreamResponseTooLarge, "UpstreamResponseTooLarge",
                            outcome: AuditOutcome.UpstreamError, upstreamStatus: audit.UpstreamStatus);
                    }
                    context.Abort(); // already streaming: the only safe option is to reset the connection
                    return;
                }
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target)
    {
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (UpstreamRequestBuilder.RelayedResponseHeaders.Contains(header.Key))
            {
                target.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}
