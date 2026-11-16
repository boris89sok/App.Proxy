namespace App.MinimalApi.Dal.SecureProxy.Models;

/// <summary>A configured upstream API (dbo.Destinations).</summary>
public class Destination
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string BaseUrl { get; set; } = null!;
    public bool IsEnabled { get; set; }
    public int TimeoutSeconds { get; set; }
    /// <summary>Comma separated list of permitted ports, e.g. "443,5001". NULL = scheme default port only.</summary>
    public string? AllowedPorts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = null!;

    public ICollection<Operation> Operations { get; set; } = new List<Operation>();
}

/// <summary>An explicitly allowed action (dbo.Operations).</summary>
public class Operation
{
    public int Id { get; set; }
    public string OperationKey { get; set; } = null!;
    public int DestinationId { get; set; }
    public string HttpMethod { get; set; } = "GET";
    public string PathTemplate { get; set; } = null!;
    public bool IsEnabled { get; set; }
    public int MaxRequestBytes { get; set; }
    public int MaxResponseBytes { get; set; }
    public bool AllowUnknownParameters { get; set; }
    public bool RequiresEncryption { get; set; }
    public int? EncryptionProfileId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = null!;

    public Destination Destination { get; set; } = null!;
    public EncryptionProfile? EncryptionProfile { get; set; }
    public ICollection<ParameterRule> ParameterRules { get; set; } = new List<ParameterRule>();
    public ICollection<HeaderRule> HeaderRules { get; set; } = new List<HeaderRule>();
}

/// <summary>Validation rule for one caller supplied parameter (dbo.ParameterRules).</summary>
public class ParameterRule
{
    public int Id { get; set; }
    public int OperationId { get; set; }
    public string Name { get; set; } = null!;
    public bool IsRequired { get; set; }
    /// <summary>string | int | guid | bool | date</summary>
    public string DataType { get; set; } = null!;
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public long? MinValue { get; set; }
    public long? MaxValue { get; set; }
    public string? RegexPattern { get; set; }
    /// <summary>Path | Query</summary>
    public string Location { get; set; } = null!;

    public Operation Operation { get; set; } = null!;
}

/// <summary>Explicit header forwarding allowlist entry (dbo.HeaderRules).</summary>
public class HeaderRule
{
    public int Id { get; set; }
    public int OperationId { get; set; }
    public string HeaderName { get; set; } = null!;
    public bool IsRequired { get; set; }
    public int? MaxLength { get; set; }
    public string? RegexPattern { get; set; }

    public Operation Operation { get; set; } = null!;
}

/// <summary>
/// Encryption profile metadata (dbo.EncryptionProfiles).
/// Holds only a reference to a secret in the key source - never key material.
/// </summary>
public class EncryptionProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string KeyReference { get; set; } = null!;
    public int KeyVersion { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = null!;
}

/// <summary>Metadata-only execution audit record (dbo.RequestLogs). Never holds bodies, headers or secrets.</summary>
public class RequestLog
{
    public long Id { get; set; }
    public string CorrelationId { get; set; } = null!;
    public string? OperationKey { get; set; }
    public int? DestinationId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public bool Forwarded { get; set; }
    public int? UpstreamStatus { get; set; }
    public int ProxyStatus { get; set; }
    public string Outcome { get; set; } = null!;
    public string? Reason { get; set; }
}
