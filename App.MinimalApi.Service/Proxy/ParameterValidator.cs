namespace App.MinimalApi.Service.Proxy;

/// <summary>Canonicalised, validated parameter values ready to be placed into the upstream URL.</summary>
public sealed record ValidatedParameters(
    IReadOnlyDictionary<string, string> Path,
    IReadOnlyList<KeyValuePair<string, string>> Query);

/// <summary>
/// Validates caller parameters against the configured ParameterRules.
/// Values are canonicalised (e.g. "+005" -> "5", any GUID format -> "D" format) so that what is
/// validated is exactly what is forwarded.
/// </summary>
public static class ParameterValidator
{
    /// <summary>Spec rule: every request's parameters must contain this key.</summary>
    public const string RequiredUrlIdKey = "urlId";

    private const int DefaultMaxStringLength = 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    public static ValidatedParameters Validate(OperationSnapshot operation, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.ContainsKey(RequiredUrlIdKey))
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidParameters, "MissingUrlId",
                [$"{RequiredUrlIdKey}: required"]);
        }

        var errors = new List<string>();
        var path = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = new List<KeyValuePair<string, string>>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in operation.Parameters)
        {
            known.Add(rule.Name);
            var isPath = rule.Location switch
            {
                "Path" => true,
                "Query" => false,
                _ => throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "UnsupportedParameterLocation")
            };

            if (!input.TryGetValue(rule.Name, out var raw))
            {
                if (rule.IsRequired) errors.Add($"{rule.Name}: required");
                continue;
            }

            var value = Canonicalize(rule, raw);
            if (value is null)
            {
                errors.Add($"{rule.Name}: invalid");
                continue;
            }

            if (isPath)
            {
                EnsureSafePathSegment(value);
                path[rule.Name] = value;
            }
            else
            {
                query.Add(new(rule.Name, value));
            }
        }

        var unknown = input.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0 && !operation.AllowUnknownParameters)
        {
            // Unknown names are caller supplied: they are reported back to the caller (sanitised) but
            // only counted in the audit log.
            errors.AddRange(unknown.Take(10).Select(n => $"{EnvelopeParser.SafeName(n)}: not allowed"));
            throw new ProxyRejectedException(ProxyErrorCode.InvalidParameters, $"UnknownParameters:{unknown.Count}", errors);
        }
        // When unknown parameters are allowed they are DROPPED - never forwarded.

        if (errors.Count > 0)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidParameters,
                "InvalidParameters:" + string.Join(',', errors.Select(e => e.Split(':')[0]).Take(5)), errors);
        }

        return new ValidatedParameters(path, query);
    }

    /// <summary>
    /// Rejects anything that could change the path structure, even though values are percent-encoded
    /// afterwards (defense in depth against double decoding by upstream servers).
    /// </summary>
    public static void EnsureSafePathSegment(string value)
    {
        if (value.Length == 0
            || value.Contains("..", StringComparison.Ordinal)
            || value.IndexOfAny(['/', '\\', '%', '?', '#', ';', ':']) >= 0
            || value.Any(char.IsControl))
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidPathParameter, "PathTraversalAttempt");
        }
    }

    private static string? Canonicalize(ParameterRuleSnapshot rule, JsonElement raw)
    {
        string? value = rule.DataType switch
        {
            "string" => CanonicalString(rule, raw),
            "int" => CanonicalInt(rule, raw),
            "guid" => raw.ValueKind == JsonValueKind.String && Guid.TryParse(raw.GetString(), out var g)
                ? g.ToString("D") : null,
            "bool" => raw.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String when bool.TryParse(raw.GetString(), out var b) => b ? "true" : "false",
                _ => null
            },
            "date" => raw.ValueKind == JsonValueKind.String
                      && DateOnly.TryParseExact(raw.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            _ => throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "UnsupportedParameterType")
        };

        if (value is null || string.IsNullOrEmpty(rule.RegexPattern))
        {
            return value;
        }

        try
        {
            return GetRegex(rule.RegexPattern).IsMatch(value) ? value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? CanonicalString(ParameterRuleSnapshot rule, JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.String) return null;
        var s = raw.GetString()!;
        if (s.Any(char.IsControl)) return null;
        if (rule.MinLength is { } min && s.Length < min) return null;
        if (s.Length > (rule.MaxLength ?? DefaultMaxStringLength)) return null;
        return s;
    }

    private static string? CanonicalInt(ParameterRuleSnapshot rule, JsonElement raw)
    {
        long number;
        switch (raw.ValueKind)
        {
            case JsonValueKind.Number when raw.TryGetInt64(out number):
                break;
            case JsonValueKind.String when long.TryParse(raw.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number):
                break;
            default:
                return null;
        }

        if (rule.MinValue is { } min && number < min) return null;
        if (rule.MaxValue is { } max && number > max) return null;
        return number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Configured patterns are always evaluated as a full match with a timeout (ReDoS protection).</summary>
    internal static Regex GetRegex(string pattern)
    {
        try
        {
            return RegexCache.GetOrAdd(pattern, p => new Regex($"^(?:{p})$",
                RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout));
        }
        catch (ArgumentException)
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "InvalidConfiguredRegex");
        }
    }
}
