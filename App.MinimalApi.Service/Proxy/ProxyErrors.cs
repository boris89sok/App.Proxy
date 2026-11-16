namespace App.MinimalApi.Service.Proxy;

/// <summary>Header names used across the solution (defined once).</summary>
public static class ProxyHeaders
{
    public const string CorrelationId = "X-Correlation-Id";
    public const string ApiKey = "X-Api-Key";

    /// <summary>Tells the caller who produced the response: <see cref="SourceProxy"/> or <see cref="SourceUpstream"/>.</summary>
    public const string Source = "X-Proxy-Source";
    public const string SourceProxy = "proxy";
    public const string SourceUpstream = "upstream";
}

/// <summary>Audit outcome categories stored in dbo.RequestLogs.Outcome.</summary>
public static class AuditOutcome
{
    public const string Success = "Success";
    public const string Rejected = "Rejected";
    public const string UpstreamError = "UpstreamError";
    public const string Timeout = "Timeout";
    public const string ClientCancelled = "ClientCancelled";
    public const string Failed = "Failed";
}

/// <summary>
/// Thrown to stop processing with a public error code. Used by the proxy pipeline and by the configuration
/// admin service. Carries an internal audit reason (safe for the audit log - never values, keys or bodies).
/// </summary>
public sealed class ProxyRejectedException(
    ProxyErrorCode code,
    string auditReason,
    IReadOnlyList<string>? errors = null,
    string outcome = AuditOutcome.Rejected,
    int? upstreamStatus = null,
    Exception? inner = null) : Exception(code.ToString(), inner)
{
    public ProxyErrorCode Code { get; } = code;
    public string AuditReason { get; } = auditReason;
    public IReadOnlyList<string>? Errors { get; } = errors;
    public string Outcome { get; } = outcome;
    public int? UpstreamStatus { get; } = upstreamStatus;
}

/// <summary>
/// The single error contract of the application: one status table, one message table and one writer,
/// used by the proxy pipeline, the admin endpoints and GlobalExceptionHandler.
/// </summary>
public static class ProxyErrors
{
    /// <summary>Non-standard status used for "client closed request" (nothing is written to the caller).</summary>
    public const int ClientClosedRequest = 499;

    public static int StatusFor(ProxyErrorCode code) => code switch
    {
        ProxyErrorCode.InvalidEnvelope => StatusCodes.Status400BadRequest,
        ProxyErrorCode.UnknownEnvelopeField => StatusCodes.Status400BadRequest,
        ProxyErrorCode.InvalidRequest => StatusCodes.Status400BadRequest,
        ProxyErrorCode.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        ProxyErrorCode.RequestTooLarge => StatusCodes.Status413PayloadTooLarge,
        ProxyErrorCode.UnknownOperation => StatusCodes.Status404NotFound,
        ProxyErrorCode.OperationDisabled => StatusCodes.Status403Forbidden,
        ProxyErrorCode.Forbidden => StatusCodes.Status403Forbidden,
        ProxyErrorCode.DestinationUnavailable => StatusCodes.Status503ServiceUnavailable,
        ProxyErrorCode.OperationMisconfigured => StatusCodes.Status500InternalServerError,
        ProxyErrorCode.EncryptionRequired => StatusCodes.Status400BadRequest,
        ProxyErrorCode.InvalidEncryptedPayload => StatusCodes.Status400BadRequest,
        ProxyErrorCode.InvalidParameters => StatusCodes.Status400BadRequest,
        ProxyErrorCode.InvalidPathParameter => StatusCodes.Status400BadRequest,
        ProxyErrorCode.InvalidHeader => StatusCodes.Status400BadRequest,
        ProxyErrorCode.UpstreamTimeout => StatusCodes.Status504GatewayTimeout,
        ProxyErrorCode.UpstreamUnavailable => StatusCodes.Status502BadGateway,
        ProxyErrorCode.UpstreamResponseTooLarge => StatusCodes.Status502BadGateway,
        ProxyErrorCode.UpstreamError => StatusCodes.Status502BadGateway,
        ProxyErrorCode.AuditUnavailable => StatusCodes.Status503ServiceUnavailable,
        ProxyErrorCode.ConfigurationConflict => StatusCodes.Status409Conflict,
        ProxyErrorCode.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status500InternalServerError
    };

    /// <summary>Generic public messages. Never include hostnames, URLs, key names or exception text.</summary>
    public static string MessageFor(ProxyErrorCode code) => code switch
    {
        ProxyErrorCode.InvalidEnvelope => "The request envelope is invalid.",
        ProxyErrorCode.UnknownEnvelopeField => "The request envelope contains a field that is not allowed.",
        ProxyErrorCode.InvalidRequest => "The request is invalid.",
        ProxyErrorCode.UnsupportedMediaType => "Content-Type must be application/json.",
        ProxyErrorCode.RequestTooLarge => "The request is too large.",
        ProxyErrorCode.UnknownOperation => "The requested operation does not exist.",
        ProxyErrorCode.OperationDisabled => "The requested operation is disabled.",
        ProxyErrorCode.Forbidden => "Access denied.",
        ProxyErrorCode.DestinationUnavailable => "The destination of the operation is unavailable.",
        ProxyErrorCode.OperationMisconfigured => "The requested operation is not available.",
        ProxyErrorCode.EncryptionRequired => "The operation requires an encrypted payload.",
        ProxyErrorCode.InvalidEncryptedPayload => "The encrypted payload is invalid.",
        ProxyErrorCode.InvalidParameters => "One or more parameters are invalid.",
        ProxyErrorCode.InvalidPathParameter => "A path parameter contains characters that are not allowed.",
        ProxyErrorCode.InvalidHeader => "A request header is missing or invalid.",
        ProxyErrorCode.UpstreamTimeout => "The destination did not respond in time.",
        ProxyErrorCode.UpstreamUnavailable => "The destination could not be reached.",
        ProxyErrorCode.UpstreamResponseTooLarge => "The destination response exceeded the permitted size.",
        ProxyErrorCode.UpstreamError => "The destination returned an error.",
        ProxyErrorCode.AuditUnavailable => "The request cannot be processed right now.",
        ProxyErrorCode.ConfigurationConflict => "The configuration was modified by another request. Reload and retry.",
        ProxyErrorCode.NotFound => "The resource was not found.",
        _ => "An unexpected error occurred."
    };

    /// <summary>
    /// HTTP status of an error response: an upstream HTTP error keeps the upstream status
    /// (spec: preserve upstream status codes), everything else comes from <see cref="StatusFor"/>.
    /// </summary>
    public static int ResponseStatus(ProxyErrorCode code, int? upstreamStatus) =>
        IsFromUpstream(code, upstreamStatus) ? upstreamStatus!.Value : StatusFor(code);

    /// <summary>Writes the error response, unless the response already started or the caller went away.</summary>
    public static async Task WriteAsync(
        HttpContext context,
        ProxyErrorCode code,
        IReadOnlyList<string>? errors = null,
        int? upstreamStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (context.Response.HasStarted || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var source = IsFromUpstream(code, upstreamStatus) ? ProxyHeaders.SourceUpstream : ProxyHeaders.SourceProxy;
        var response = context.Response;
        response.StatusCode = ResponseStatus(code, upstreamStatus);
        response.Headers[ProxyHeaders.Source] = source;
        response.Headers.CacheControl = "no-store";

        var body = new ProxyErrorResponse
        {
            Code = code.ToString(),
            Message = MessageFor(code),
            CorrelationId = context.TraceIdentifier,
            Source = source,
            UpstreamStatus = upstreamStatus,
            Errors = errors
        };

        try
        {
            await response.WriteAsJsonAsync(body, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // caller went away
        }
    }

    private static bool IsFromUpstream(ProxyErrorCode code, int? upstreamStatus) =>
        code == ProxyErrorCode.UpstreamError && upstreamStatus is not null;
}
