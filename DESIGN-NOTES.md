# Design Notes: Secure Proxy

Guiding rule: **configuration-driven, secure by default, fail closed.** If any check can't positively confirm that a request is allowed, the request is rejected, and nothing is sent upstream.

## 1. Architecture

The design keeps the existing layering and patterns: Carter modules, `AddServices` DI, `GenericRepository<,>`, a DB-first `DbContext`, and the existing `GlobalExceptionHandler` and `EnvOptions`. To avoid parallel infrastructure there is **one options class** (`EnvOptions`, bound from the `Proxy` section with one `AddValidatedOptions` registration pattern) and **one error contract** (`ProxyErrors`: one status table, one message table and one writer, used by the proxy pipeline and `GlobalExceptionHandler`). The supplied Region sample (endpoints, service, `NORTHWND` context) was removed because it is outside the scope of the specification. The API exposes **exactly one endpoint**, `POST /api/proxy/execute`, which is the only one the specification requires. There is no admin API and no health endpoint. Configuration is changed through `ProxyConfigAdminService` or directly in SQL, never through the public API.

```
Webapi   CorrelationIdMiddleware -> Serilog request log -> ExceptionHandler -> AuthN (ApiKey + JWT) -> AuthZ -> RateLimiter
         ProxyEndpoints  POST /api/proxy/execute   (the only endpoint)
Service  ProxyExecutionService (pipeline)
           EnvelopeParser | ProxyConfigCache | AesGcmPayloadDecryptor(ISecretKeyProvider) | ParameterValidator
           UpstreamRequestBuilder | IHttpClientFactory("proxy-upstream" + circuit breaker) | RequestAuditor -> AuditWriterService
         ProxyConfigAdminService (configuration updates: rowversion concurrency, cache invalidation; not exposed over HTTP)
Dal      SecureProxyContext + OperationRepository / DestinationRepository / RequestLogRepository
SQL      Destinations, Operations, ParameterRules, HeaderRules, EncryptionProfiles (temporal) + RequestLogs
```

## 2. Request trace: `POST /api/proxy/execute`

| Step | Where | What happens | Failure |
|---|---|---|---|
| 0 | `CorrelationIdMiddleware` | Reuses a well-formed `X-Correlation-Id` (`^[A-Za-z0-9-]{8,64}$`), otherwise generates one. Sets `TraceIdentifier` and the log context, and echoes it in the response | n/a |
| 1 | `CompositeAuthenticationHandler` | **Both** `X-Api-Key` (SHA-256 plus constant-time compare against configured hashes) and a JWT (HS256, issuer, audience, lifetime) must succeed | 401, never reaches the pipeline |
| 2 | Rate limiter | Global concurrency limit plus a per-API-client token bucket | 429 |
| 3 | `ReadEnvelopeAsync` | `Content-Type: application/json` (UTF-8). Bounded read up to `GlobalMaxRequestBytes` (Content-Length checked first, then a counting read) | 415 / 413 |
| 4 | `EnvelopeParser` | Strict shape: only `clientId`, `json`, `encrypted`, with exact names. Duplicate keys rejected (prevents smuggling). `clientId` format checked. Exactly one of `json`/`encrypted` | 400 `InvalidEnvelope` / `UnknownEnvelopeField` |
| 5 | `ProxyConfigCache` | Snapshot of operation, destination, rules, and profile. Keys match ordinally | 404 `UnknownOperation` |
| 6 | `EnsureExecutable` | Operation enabled. Destination present and enabled. Method in the **code** allowlist {GET}. Timeout in range | 403 / 503 / 500 `OperationMisconfigured` |
| 7 | size | Body ≤ `Operations.MaxRequestBytes` | 413 |
| 8 | `AesGcmPayloadDecryptor` | When `RequiresEncryption`: plaintext rejected. AES-256-GCM with 12-byte nonce, 16-byte tag, **AAD = clientId**. Key from `ISecretKeyProvider`; key buffers zeroed | 400 `EncryptionRequired` / `InvalidEncryptedPayload` |
| 9 | `ParameterValidator` | `urlId` required (spec). Each rule: required, type, length/range, full-match regex with a 100 ms timeout. Values **canonicalized** (`"+005"` becomes `5`). Unknown parameters rejected, or **dropped** if allowed | 400 `InvalidParameters` |
| 10 | `UpstreamRequestBuilder.BuildUri` | Base URL (https only, no userinfo/query) + validated template + `Uri.EscapeDataString` per segment. Path values containing `/ \ .. % ? # ; :` or control characters are rejected. **Final check:** same scheme, host, and port as the base; path under the base path; port in `AllowedPorts` (null means the scheme's default only) | 400 `InvalidPathParameter` / 500 |
| 11 | `SelectHeaders` | Only `HeaderRules` entries, validated (single value, length, regex, printable ASCII). A hard denylist always wins (credentials, cookies, hop-by-hop, routing headers, `Proxy-*`, `X-Forwarded-*`, `Sec-*`). Headers the caller names in `Connection:` are dropped | 400 `InvalidHeader` |
| 12 | `TryBeginForwardAsync` | Mandatory audit mode only: persist an `InProgress` row first | 503 `AuditUnavailable` |
| 13 | `ForwardAsync` | `IHttpClientFactory` client. Linked CTS = caller abort + destination timeout. `ResponseHeadersRead`. Response streamed with a byte counter. Only allowlisted response headers are relayed | 504 / 502 / 503 (see §5) |
| 14 | `finally` | One structured log line and one audit record for **every** attempt | n/a |

Steps 3–12 only read trusted configuration and validate input. The `HttpClient` is created and used in step 13 only, so the "never reaches the destination" guarantee is structural. Every rejection test proves it with the recording mock handler.

## 3. Security decisions

- **The caller can't influence routing.** URL, method, and path come only from `Destinations` and `Operations`. The envelope parser rejects any unknown field. `AllowUnknownParameters=1` drops unknown parameters; it never forwards them.
- **Path traversal: defense in depth.** Typed canonicalization comes first. Then dangerous characters are rejected even though values are escaped afterwards (some upstreams double-decode `%2e%2e%2f`). Then the final URI is compared with the trusted base.
- **Redirects disabled** (`AllowAutoRedirect=false`). A redirect could otherwise send the request to a host that isn't configured. Upstream `Location` isn't relayed.
- **No cookie container, no system proxy, no auto-decompression.** Callers never share cookies. Size limits apply to bytes on the wire (no decompression bombs).
- **Upstream 5xx bodies are not relayed.** The status is preserved and the proxy returns its own error body with `source: "upstream"`, which avoids leaking upstream stack traces or hostnames. 2xx–4xx bodies are relayed (bounded), because they're the business answer. This is a deliberate trade-off.
- **Encryption.** The database stores only `KeyReference` + `KeyVersion` + `Algorithm`. `ISecretKeyProvider` resolves the key from `Proxy:EncryptionKeys:{ref}:v{n}` (user-secrets or environment variables locally; Key Vault configuration provider or HSM in production; swap the implementation). The AAD binds a ciphertext to its operation, so a ciphertext can't be replayed against another operation. All crypto failures return the same generic error. Key rotation: add `v2`, update `KeyVersion` (history is kept), and remove `v1` after callers have migrated.
- **Authentication.** The API key identifies the calling application, and the JWT identifies the user or service and its roles. Both are required: a stolen key alone or a stolen token alone isn't enough. Keys are stored only as SHA-256 hashes and compared in constant time. Settings are validated at startup (fail closed).
- **Errors never contain** hostnames, URLs, key names, exception text, or stack traces. Callers get a stable `code`, a generic message, and the `correlationId`.

## 4. Reliability and performance

### Timeouts and cancellation
- `HttpClient.Timeout = Infinite`. The effective timeout is `Destinations.TimeoutSeconds` (1–120, CHECK constraint plus a code check) through a `CancellationTokenSource(TimeSpan, TimeProvider)`, linked with `HttpContext.RequestAborted`.
- **Distinguishing the cause:** if `RequestAborted` fired, the outcome is `ClientCancelled` (499, nothing written). If the timeout CTS fired, it's `Timeout` and a **504**. The token stays linked while the body streams, so a slow body is also bounded (504 before headers are sent, connection abort after).

### HttpClient lifecycle
A named `IHttpClientFactory` client with `SocketsHttpHandler` and `PooledConnectionLifetime = 2 min` (picks up DNS changes). There's no per-request `HttpClient`.

### Streaming and size limits
- Request: hard global cap (Kestrel `MaxRequestBodySize` + `IHttpMaxRequestBodySizeFeature` + a counting read), then the per-operation cap. The envelope is small and bounded, so it's buffered (at most cap bytes) for strict parsing.
- Response: `ResponseHeadersRead`, then `Content-Length` is checked against `MaxResponseBytes` before anything is written (clean 502), then a 16 KB pooled-buffer copy with a byte counter. If a chunked response exceeds the limit after streaming has started, the only safe option is to abort the connection; it's audited as `UpstreamResponseTooLarge`.

### Retries and idempotency
- The proxy performs **no retries**: no Polly retry, no SQL retry strategy. Only GET is allowed today (it's idempotent), but automatic retries multiply load on a struggling upstream and blur the latency budget. The circuit breaker is the only resilience strategy.
- Safe to retry (by the caller, or by the proxy if ever enabled): failures where the request **wasn't sent** (connect refused, DNS, circuit open), and timeouts or 502/503/504 **for GET only**.
- Never safe to retry blindly: any non-idempotent method after the request may have reached the upstream (timeouts, connection resets, 5xx).
- **Plan for POST/PUT (stage 2):** require an `Idempotency-Key` header from the caller (validated format), forwarded to upstreams that support it. Also store `(OperationId, IdempotencyKey, RequestHash, Status, ResponseMetadata, ExpiresAt)` with a UNIQUE constraint before forwarding. A duplicate with the same hash gets the stored outcome; the same key with a different hash gets 422; a key still in progress gets 409. This deduplicates even when the upstream has no idempotency support.

### Configuration caching and consistency model
- `ProxyConfigCache` (`IMemoryCache`) stores immutable snapshots. TTL is `ConfigCacheSeconds` (default 30 s). Not-found results are cached for `NegativeCacheSeconds` (5 s), which protects SQL from key enumeration.
- **Consistency:** a change made through `ProxyConfigAdminService` invalidates the entry **immediately on that instance**. Other instances, and direct SQL edits, converge within the TTL (bounded staleness). For a multi-instance deployment, publish invalidations (Redis pub/sub or SQL query notifications); the `IProxyConfigCache.Invalidate` seam already exists.
- An emergency kill switch is effective within the TTL. Set `ConfigCacheSeconds=0` for strong consistency at the cost of one query per request.

### Concurrency
- All configuration tables have a SQL Server `rowversion`. `ProxyConfigAdminService.Get*` returns it in Base64. `Update*` requires the version the caller read: EF sets `OriginalValue` to that version and adds it to the `WHERE` clause. If zero rows are affected, EF raises `DbUpdateConcurrencyException`. The service turns it into `ProxyErrorCode.ConfigurationConflict` (**409 Conflict** in the error contract), and nothing is written. `ConfigConcurrencyTests` covers this with an interceptor that emulates rowversion on EF InMemory.
- The update service is deliberately not exposed over HTTP, because the specification requires only `POST /api/proxy/execute`. If an admin API is added later, it maps `ConfigurationConflict` to 409 through `ProxyErrors` and carries the rowversion in `ETag`/`If-Match`.
- **In-flight requests when something is disabled:** a request that already took its snapshot runs to completion. Requests aren't aborted mid-flight, because cutting a half-delivered response is worse than completing an already-authorized read. New requests are rejected according to the cache consistency model above.

### Resilience
- **Rate limiting:** a token bucket per API client (`RateLimiting:TokenLimit/TokensPerSecond`), giving 429.
- **Bounded concurrency:** a global concurrency limiter with no queue (`MaxConcurrentRequests`), giving 429 immediately instead of piling up threads and sockets.
- **Circuit breaker:** one per destination authority (`SelectPipelineByAuthority`). It opens on a failure ratio of 5xx, 408, 429, or connection errors. When open, the proxy returns 503 `DestinationUnavailable` **without touching the network** (`Forwarded=false`). No retry or extra timeout strategy is added, so nothing can be sent twice.
- Per-IP limits for unauthenticated floods belong at the edge (WAF or gateway). Authentication rejects those requests cheaply before the pipeline.

## 5. Error contract

`ProxyErrorResponse { code, message, correlationId, source, upstreamStatus?, errors? }` plus the headers `X-Proxy-Source: proxy|upstream` and `Cache-Control: no-store`. This is the **only** error shape of the API: the proxy pipeline and `GlobalExceptionHandler` (unhandled exceptions) both write it through `ProxyErrors.WriteAsync`.

The proxy pipeline writes expected rejections itself instead of rethrowing to the exception handler: the pipeline must fill the audit record and may relay an upstream status, and in .NET 8 the exception-handler middleware logs every exception as an unhandled error.

| Situation | Status | code | source |
|---|---|---|---|
| Unknown operation | 404 | `UnknownOperation` | proxy |
| Disabled operation | 403 | `OperationDisabled` | proxy |
| Missing or disabled destination, open circuit | 503 | `DestinationUnavailable` | proxy |
| Invalid envelope / field not allowed | 400 | `InvalidEnvelope` / `UnknownEnvelopeField` | proxy |
| Invalid parameters / path / header | 400 | `InvalidParameters` / `InvalidPathParameter` / `InvalidHeader` | proxy |
| Invalid content type | 415 | `UnsupportedMediaType` | proxy |
| Request too large | 413 | `RequestTooLarge` | proxy |
| Invalid encrypted payload / plaintext sent to an encrypted operation | 400 | `InvalidEncryptedPayload` / `EncryptionRequired` | proxy |
| Misconfiguration (bad base URL, port, method, key unavailable) | 500 | `OperationMisconfigured` | proxy |
| Upstream timeout | 504 | `UpstreamTimeout` | proxy |
| Upstream connection failure | 502 | `UpstreamUnavailable` | proxy |
| Upstream response too large | 502 | `UpstreamResponseTooLarge` | proxy |
| Upstream 2xx–4xx | as upstream | upstream body | upstream |
| Upstream 5xx | as upstream | `UpstreamError` (body replaced) | upstream |
| Configuration concurrency conflict | 409 | `ConfigurationConflict` | proxy |
| Unhandled exception (GlobalExceptionHandler) | 400 / 404 / 502 / 500 | `InvalidRequest` / `NotFound` / `UpstreamUnavailable` / `InternalError` | proxy |

## 6. Audit, logging, and observability

**Audit policy.** Every execution attempt that passed authentication produces exactly one `RequestLogs` row: success, rejection (including envelope errors before an operation is identified, with `OperationKey = NULL`), upstream error, timeout, or client cancellation. Authentication and authorization failures (401/403/429 before the pipeline) aren't execution attempts. They're recorded by the structured request log (Serilog request logging) with the correlation id.

Each row stores correlation id, operation key, destination id, start and completion time, duration, whether it was forwarded, upstream status, proxy status, outcome, and a **reason code**. It never stores bodies, headers, parameter values, keys, or tokens. Reasons are internal codes such as `PathTraversalAttempt` or `UnknownParameters:2`. Caller-supplied names appear only after passing a strict format check.

**When SQL Server is unavailable.** The mode is configurable through `Proxy:Audit:Mode`:
- `BestEffort` (default, **buffered**): rows go into a bounded in-memory channel (10,000 by default). A background writer inserts in batches and retries with exponential backoff (1 s, 2 s, 4 s). Rows that still fail, or that don't fit in the channel, are written to the structured log as `AuditFallback` events with the same metadata, and a dropped-row counter is incremented. **Consequence:** traffic keeps flowing during a database outage, and the audit trail survives in the log pipeline. However, records are lost if the process crashes while the queue is non-empty, and the database trail has gaps that must be backfilled from logs.
- `Mandatory`: an `InProgress` row is inserted **synchronously before forwarding**. If that fails, the request is rejected with 503 and **not forwarded**; the row is updated with the outcome at the end. **Consequence:** the audit trail is complete for every forwarded request, but a SQL outage becomes a proxy outage. Use this for operations with regulatory audit requirements.

**Structured logging.** Serilog writes compact JSON to the console with `CorrelationId` on every event. One `Proxy execution {Outcome} {OperationKey} ...` event is logged per request. `System.Net.Http.HttpClient` logging is set to Warning because its Information events include full URLs. `UseSerilogRequestLogging` records method, path, status, and elapsed time, but never headers or bodies. There's no `traceparent` to upstream: only allowlisted headers and `X-Correlation-Id`.

## 7. Configuration history

The five configuration tables are **system-versioned temporal tables** (`002_Extensions.sql`). SQL Server keeps every previous row version with its validity period automatically, so direct SQL changes are covered too. `UpdatedAt` and `UpdatedBy` (the JWT `sub`) record who made a change through `ProxyConfigAdminService`. These tables hold only metadata and key references, so the history can't contain secrets.

```sql
SELECT Id, OperationKey, IsEnabled, PathTemplate, UpdatedBy, ValidFrom, ValidTo
FROM dbo.Operations FOR SYSTEM_TIME ALL WHERE Id = 1 ORDER BY ValidFrom;
```

## 8. Testing strategy

- `WebApplicationFactory<Program>` runs the **real** middleware, auth, rate limiter, and pipeline. Only SQL Server is replaced (EF InMemory, plus a rowversion emulation interceptor) and the network (`RecordingHandler`, the primary handler of the proxy client, which records every request).
- Every rejection asserts `Upstream.Requests` is empty.
- Unit tests cover the security-critical builders: template validation, port allowlist, base URL rules, canonicalization, ReDoS timeout, and the cache TTL and invalidation behavior.
- Verified manually end to end on LocalDB with real Kestrel and the DevTool mock upstream: forwarded headers, audit rows, temporal history, 409 on a stale update, and no secrets in logs.

## 9. Known limitations and next steps

- GET only (stage 1). POST/PUT need the idempotency design in §4.
- Cache invalidation is per instance. Add a distributed invalidation channel for scale-out.
- JWTs are HS256 with a shared key, for local self-containment. In production, use an identity provider (OIDC authority with asymmetric keys) and optionally bind the JWT `azp` to the API client.
- Upstream authentication (client credentials or mTLS per destination) would be another profile reference resolved through `ISecretKeyProvider`, the same pattern as encryption keys.
- The EF InMemory provider doesn't enforce SQL constraints. The SQL scripts were verified separately against LocalDB.
