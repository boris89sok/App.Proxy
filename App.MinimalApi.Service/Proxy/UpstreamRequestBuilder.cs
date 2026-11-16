namespace App.MinimalApi.Service.Proxy;

/// <summary>
/// Builds the upstream URI and header set exclusively from trusted configuration plus validated values.
/// Every check fails closed: if configuration is inconsistent, nothing is forwarded.
/// </summary>
public static partial class UpstreamRequestBuilder
{
    /// <summary>Headers that are never forwarded, even when an operation's allowlist names them.</summary>
    private static readonly HashSet<string> DeniedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        // credentials / identity of the caller towards the proxy
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", ProxyHeaders.ApiKey,
        // hop-by-hop (RFC 9110 §7.6.1) and framing
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Content-Length", "Content-Type", "Expect",
        // routing - must come from configuration only
        "Host", "Forwarded", "X-Real-IP", "X-Original-URL", "X-Rewrite-URL", "X-HTTP-Method-Override",
        // set by the proxy itself
        ProxyHeaders.CorrelationId
    };

    private static readonly string[] DeniedPrefixes = ["Proxy-", "X-Forwarded-", "Sec-"];

    /// <summary>Upstream response headers relayed to the caller (everything else is dropped, e.g. Set-Cookie, Server, Location).</summary>
    public static readonly IReadOnlySet<string> RelayedResponseHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Language", "Content-Length", "ETag", "Last-Modified", "Cache-Control", "Expires"
    };

    [GeneratedRegex(@"\{([A-Za-z0-9_]{1,100})\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^(/[A-Za-z0-9_\-.~]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralPathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9!#$&'*+\-.^_`|~]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderTokenRegex();

    public static bool IsDeniedHeader(string name) =>
        DeniedHeaders.Contains(name) || DeniedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>A template is valid when, with placeholders replaced, it is a plain absolute path without dot segments.</summary>
    public static bool IsValidPathTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 300 || !template.StartsWith('/')
            || template.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var literal = PlaceholderRegex().Replace(template, "x");
        if (!LiteralPathRegex().IsMatch(literal)) return false;

        return literal.Split('/').All(segment => segment is not "." and not "..");
    }

    public static bool TryParseBaseUrl(string? baseUrl, bool allowHttp, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        baseUri = uri;
        return true;
    }

    public static bool TryParsePorts(string? allowedPorts, out IReadOnlySet<int>? ports)
    {
        ports = null;
        if (allowedPorts is null) return true;

        var set = new HashSet<int>();
        foreach (var part in allowedPorts.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                return false;
            }
            set.Add(port);
        }

        ports = set;
        return set.Count > 0;
    }

    public static Uri BuildUri(OperationSnapshot operation, ValidatedParameters parameters, bool allowHttp)
    {
        var destination = operation.Destination
            ?? throw new ProxyRejectedException(ProxyErrorCode.DestinationUnavailable, "DestinationMissing");

        if (!TryParseBaseUrl(destination.BaseUrl, allowHttp, out var baseUri))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "InvalidDestinationBaseUrl");
        }
        if (!TryParsePorts(destination.AllowedPorts, out var ports))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "InvalidAllowedPorts");
        }
        if (!IsValidPathTemplate(operation.PathTemplate))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "InvalidPathTemplate");
        }

        var usedPathParameters = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var path = PlaceholderRegex().Replace(operation.PathTemplate, match =>
        {
            var name = match.Groups[1].Value;
            if (!parameters.Path.TryGetValue(name, out var value))
            {
                missing.Add(name);
                return string.Empty;
            }
            usedPathParameters.Add(name);
            ParameterValidator.EnsureSafePathSegment(value);
            return Uri.EscapeDataString(value);
        });

        if (missing.Count > 0)
        {
            if (missing.Any(m => !operation.Parameters.Any(r => r.Name == m && r.Location == "Path")))
            {
                throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "PlaceholderWithoutRule");
            }
            throw new ProxyRejectedException(ProxyErrorCode.InvalidParameters, "MissingPathParameter",
                missing.Select(m => $"{m}: required").ToArray());
        }
        if (parameters.Path.Keys.Any(k => !usedPathParameters.Contains(k)))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "PathRuleWithoutPlaceholder");
        }

        var builder = new StringBuilder();
        builder.Append(baseUri.GetLeftPart(UriPartial.Authority));
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        builder.Append(basePath).Append(path);
        if (parameters.Query.Count > 0)
        {
            builder.Append('?').AppendJoin('&', parameters.Query.Select(q =>
                Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value)));
        }

        if (!Uri.TryCreate(builder.ToString(), UriKind.Absolute, out var final))
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "UriBuildFailed");
        }

        // Final verification against the trusted base (defense in depth).
        var sameOrigin = final.Scheme == baseUri.Scheme
                         && string.Equals(final.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase)
                         && final.Port == baseUri.Port
                         && string.IsNullOrEmpty(final.UserInfo);
        var underBasePath = final.AbsolutePath.StartsWith(basePath + "/", StringComparison.Ordinal);
        if (!sameOrigin || !underBasePath)
        {
            throw new ProxyRejectedException(ProxyErrorCode.InvalidPathParameter, "UriEscapesDestination");
        }

        var portAllowed = ports is null ? final.IsDefaultPort : ports.Contains(final.Port);
        if (!portAllowed)
        {
            throw new ProxyRejectedException(ProxyErrorCode.OperationMisconfigured, "PortNotAllowed");
        }

        return final;
    }

    /// <summary>Selects the incoming headers that the operation explicitly allows, after validation.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SelectHeaders(OperationSnapshot operation, IHeaderDictionary incoming)
    {
        // Headers nominated by the caller's Connection header are hop-by-hop (RFC 9110 7.6.1): never forward them.
        var hopByHop = new HashSet<string>(
            incoming.Connection.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<KeyValuePair<string, string>>();
        foreach (var rule in operation.Headers)
        {
            if (!HeaderTokenRegex().IsMatch(rule.HeaderName) || IsDeniedHeader(rule.HeaderName) || hopByHop.Contains(rule.HeaderName))
            {
                // A misconfigured allowlist entry is ignored - it can never widen what is forwarded.
                continue;
            }

            if (!incoming.TryGetValue(rule.HeaderName, out var values) || values.Count == 0)
            {
                if (rule.IsRequired)
                {
                    throw new ProxyRejectedException(ProxyErrorCode.InvalidHeader, "MissingRequiredHeader:" + rule.HeaderName,
                        [$"{rule.HeaderName}: required"]);
                }
                continue;
            }

            var value = values.Count == 1 ? values[0] : null;
            var valid = value is not null
                        && value.Length <= (rule.MaxLength ?? 1024)
                        && !value.Any(c => char.IsControl(c) || c > 0x7E);
            if (valid && !string.IsNullOrEmpty(rule.RegexPattern))
            {
                try
                {
                    valid = ParameterValidator.GetRegex(rule.RegexPattern).IsMatch(value!);
                }
                catch (RegexMatchTimeoutException)
                {
                    valid = false;
                }
            }

            if (!valid)
            {
                throw new ProxyRejectedException(ProxyErrorCode.InvalidHeader, "InvalidHeader:" + rule.HeaderName,
                    [$"{rule.HeaderName}: invalid"]);
            }

            result.Add(new(rule.HeaderName, value!));
        }

        return result;
    }
}
