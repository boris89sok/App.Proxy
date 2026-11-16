# Enterprise Secure Proxy Web API

A configuration-driven, fail-closed proxy built on the existing `App.MinimalApi` solution (.NET 8, ASP.NET Core minimal APIs with Carter, EF Core, SQL Server).

A caller asks for an operation by its configured key (`clientId`). The destination URL, HTTP method, path, and forwarded headers come only from trusted configuration in SQL Server. A request is forwarded only after every validation and policy check has passed.

| Document | Contents |
|---|---|
| [DESIGN-NOTES.md](DESIGN-NOTES.md) | Architecture, request trace, security, and reliability decisions |
| [DEFECT-REPORT.md](DEFECT-REPORT.md) | Defects found in the supplied code, with root cause, fix, and test |
| [AI-USAGE.md](AI-USAGE.md) | AI usage declaration |

## Solution layout

```
App.MinimalApi.Model     DTOs and contracts (Proxy/ProxyContracts.cs)
App.MinimalApi.Dal       EF Core context and repositories for the proxy schema (SecureProxy/)
App.MinimalApi.Service   Business logic. Proxy/ holds the pipeline, validation, crypto, cache, audit, and configuration-update service
App.MinimalApi.Webapi    Host: Carter endpoints, authentication (API key + JWT), rate limiting, Serilog, exception handling
App.MinimalApi.Tests     xUnit tests (WebApplicationFactory + EF InMemory + recording mock destination)
tools/App.MinimalApi.DevTool   Local helper: generate secrets, issue JWTs, encrypt payloads, run a mock upstream
Database/                SQL Server scripts (001 = initial schema as supplied, 002 = extensions)
```

## Prerequisites

- .NET 8 SDK (a newer SDK also works because the projects target `net8.0`, but the .NET 8 runtime must be installed)
- SQL Server or LocalDB (`(localdb)\MSSQLLocalDB` is the default in `appsettings.json`)
- `sqlcmd` (or SSMS / Azure Data Studio)

## 1. Create the database

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -f 65001 -i Database\001_Initial.sql
sqlcmd -S "(localdb)\MSSQLLocalDB" -f 65001 -i Database\002_Extensions.sql
```

`002_Extensions.sql` is idempotent, so you can safely run it more than once. It adds the encryption profiles, header rules, and size limits, turns on configuration history (system-versioned temporal tables), and seeds the `customer.secure-get` encrypted operation.

To use a different server, override the connection string, for example in user-secrets:
`ConnectionStrings:SecureProxyDb`.

## 2. Configure secrets (never in appsettings.json)

```powershell
dotnet run --project tools/App.MinimalApi.DevTool -- secrets
```

This prints a new API key, an AES-256 key, and a JWT signing key, plus the four `dotnet user-secrets ... set` commands that store them for `App.MinimalApi.Webapi`. Run those commands. Keep the printed API key, AES key, and signing key: you need them to call the proxy.

What gets stored:

| Key | Value |
|---|---|
| `Security:Jwt:SigningKey` | Base64 HMAC key, at least 32 bytes |
| `Security:ApiClients:0:Name` / `:ApiKeySha256` | Client name and the SHA-256 hash of its API key (the key itself is never stored) |
| `Proxy:EncryptionKeys:customer-aes:v1` | AES-256 key that the `customer-aes` encryption profile references |

The application refuses to start if these settings are missing or invalid (fail closed).

## 3. Run

Terminal 1, the mock destination. The seed data points `CustomersApi` at `https://localhost:5001`:

```powershell
dotnet dev-certs https --trust          # once, so the proxy can validate the mock's TLS certificate
dotnet run --project tools/App.MinimalApi.DevTool -- mock-upstream --urls https://localhost:5001
```

> If you can't trust a dev certificate, run the mock with `--urls http://localhost:5001`. Then run
> `UPDATE dbo.Destinations SET BaseUrl = N'http://localhost:5001' WHERE Name = N'CustomersApi'` and set
> `Proxy__AllowHttpUpstream=true` for the proxy process. This is for local development only: the default is https-only.

Terminal 2, the proxy:

```powershell
dotnet run --project App.MinimalApi.Webapi --launch-profile http     # http://localhost:5253, Swagger at /swagger (Development only)
```

## 4. Call the proxy

```powershell
$token = dotnet run --project tools/App.MinimalApi.DevTool -- token --signing-key <signing key>
$headers = @{ "X-Api-Key" = "<api key>"; "Authorization" = "Bearer $token" }

# Forwarded to GET https://localhost:5001/customers/5
Invoke-RestMethod -Method Post http://localhost:5253/api/proxy/execute -Headers $headers `
  -ContentType "application/json" -Body '{"clientId":"customer.get","json":{"urlId":5}}'

# Encrypted operation
$body = dotnet run --project tools/App.MinimalApi.DevTool -- encrypt --key <aes key> --client-id customer.secure-get --json '{\"urlId\":42}'
Invoke-RestMethod -Method Post http://localhost:5253/api/proxy/execute -Headers $headers -ContentType "application/json" -Body $body
```

The API exposes only `POST /api/proxy/execute`, as the specification requires. More samples, including rejected requests, are in
[App.MinimalApi.Webapi/App.MinimalApi.Webapi.http](App.MinimalApi.Webapi/App.MinimalApi.Webapi.http).

What to look at afterwards:

```sql
SELECT * FROM dbo.RequestLogs ORDER BY Id DESC;                                -- audit trail
SELECT * FROM dbo.Operations FOR SYSTEM_TIME ALL WHERE Id = 1 ORDER BY ValidFrom; -- configuration history
```

## 5. Tests

```powershell
dotnet test App.MinimalApi.slnx
```

The tests need no database or network. They host the real pipeline in memory with EF InMemory, and a recording mock destination replaces the network. Every rejection test asserts that the destination received **no** request; the status code alone is not taken as proof. The 17 required scenarios are in `ProxyForwardingTests`, `OperationPolicyTests`, `ParameterAndHeaderTests`, `EncryptionTests`, `ConfigConcurrencyTests`, and `AuditTests`. There are also authentication, legacy-defect regression, and unit tests.

| # | Required scenario | Test(s) |
|---|---|---|
| 1 | Valid operation forwarded | `ProxyForwardingTests.Valid_configured_operation_is_forwarded_to_the_configured_destination` |
| 2 | Unknown operation | `OperationPolicyTests.Unknown_operation_is_rejected` |
| 3 | Disabled operation | `OperationPolicyTests.Disabled_operation_is_rejected`, `..._disabled_after_it_worked_...` |
| 4 | Disabled destination | `OperationPolicyTests.Disabled_destination_is_rejected`, `Missing_destination_is_rejected` |
| 5 | Caller URL cannot override | `ProxyForwardingTests.Caller_supplied_destination_fields_in_envelope_are_rejected`, `Caller_supplied_url_inside_parameters_*` |
| 6 | Invalid/disallowed method | `ProxyForwardingTests.Caller_supplied_http_method_is_rejected`, `Proxy_endpoint_only_accepts_POST`, `Operation_configured_with_a_disallowed_http_method_fails_closed` |
| 7 | Missing required parameters | `ParameterAndHeaderTests.Missing_urlId_is_rejected`, `Missing_required_query_parameter_is_rejected` |
| 8 | Unknown parameters per policy | `ParameterAndHeaderTests.Unknown_parameters_are_rejected_*`, `Unknown_parameters_are_dropped_*` |
| 9 | Path traversal | `ParameterAndHeaderTests.Path_traversal_attempts_are_rejected` (11 variants) |
| 10 | Disallowed headers not forwarded | `ParameterAndHeaderTests.Only_allowlisted_headers_are_forwarded`, `..._hop_by_hop_...` |
| 11 | Invalid AES-GCM tag | `EncryptionTests.Invalid_authentication_tag_is_rejected` (plus tampered ciphertext, wrong AAD, wrong key) |
| 12 | Invalid encrypted payload never forwarded | `EncryptionTests.Invalid_encrypted_payloads_are_never_forwarded`, `Authentic_but_invalid_decrypted_content_*` |
| 13 | Upstream timeout | `ProxyForwardingTests.Upstream_timeout_returns_504_gateway_timeout` |
| 14 | Upstream HTTP errors | `ProxyForwardingTests.Upstream_4xx_*`, `Upstream_5xx_*`, `Upstream_connection_failure_*` |
| 15 | Cancellation propagated | `ProxyForwardingTests.Caller_cancellation_is_propagated_to_the_upstream_call` |
| 16 | Concurrency conflicts | `ConfigConcurrencyTests.*` (a stale update is a 409 `ConfigurationConflict` with no overwrite, for operations and destinations) |
| 17 | Audit records | `AuditTests.*`, `MandatoryAuditTests.*` |
