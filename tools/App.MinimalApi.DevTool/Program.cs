// Local development helper for the Secure Proxy (NOT part of the deployed service).
//
//   secrets                                   generate API key / JWT signing key / AES key + user-secrets commands
//   token   --signing-key <b64> [--sub S] [--issuer I] [--audience A] [--minutes N]
//   encrypt --key <b64> --client-id <op> --json '{"urlId":5}'
//   mock-upstream [--urls https://localhost:5001]

var command = args.FirstOrDefault();
string Arg(string name, string? fallback = null) =>
    args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback ?? throw new ArgumentException($"{name} is required");

switch (command)
{
    case "secrets":
    {
        var apiKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var apiKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var aesKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string project = "App.MinimalApi.Webapi";

        Console.WriteLine($"# API key for the caller (keep it secret, only its hash is configured):\n{apiKey}\n");
        Console.WriteLine($"# AES-256 key for encryption profile 'customer-aes' v1 (callers encrypt with it):\n{aesKey}\n");
        Console.WriteLine($"# JWT signing key (used by 'token'):\n{signingKey}\n");
        Console.WriteLine("# Store them with user-secrets:");
        Console.WriteLine($"dotnet user-secrets --project {project} set \"Security:Jwt:SigningKey\" \"{signingKey}\"");
        Console.WriteLine($"dotnet user-secrets --project {project} set \"Security:ApiClients:0:Name\" \"demo-client\"");
        Console.WriteLine($"dotnet user-secrets --project {project} set \"Security:ApiClients:0:ApiKeySha256\" \"{apiKeyHash}\"");
        Console.WriteLine($"dotnet user-secrets --project {project} set \"Proxy:EncryptionKeys:customer-aes:v1\" \"{aesKey}\"");        break;
    }

    case "token":
    {
        var claims = new List<Claim> { new("sub", Arg("--sub", "dev-user")) };

        var token = new JwtSecurityToken(
            issuer: Arg("--issuer", "https://auth.example.internal"),
            audience: Arg("--audience", "secure-proxy"),
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(int.Parse(Arg("--minutes", "60"))),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(Arg("--signing-key"))), SecurityAlgorithms.HmacSha256));
        Console.WriteLine(new JwtSecurityTokenHandler().WriteToken(token));
        break;
    }

    case "encrypt":
    {
        var clientId = Arg("--client-id");
        var payload = AesGcmPayloadDecryptor.Encrypt(
            Encoding.UTF8.GetBytes(Arg("--json")), Convert.FromBase64String(Arg("--key")), clientId);
        Console.WriteLine(JsonSerializer.Serialize(new { clientId, encrypted = payload }));
        break;
    }

    case "mock-upstream":
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(Arg("--urls", "https://localhost:5001"));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Show exactly what reached the destination (header NAMES only).
            Console.WriteLine($"[upstream] {context.Request.Method} {context.Request.Path}{context.Request.QueryString} " +
                              $"headers=[{string.Join(", ", context.Request.Headers.Keys.Order())}]");
            await next();
        });
        app.MapGet("/customers/{id:int}", (int id) => id == 404
            ? Results.NotFound(new { error = "customer not found" })
            : Results.Ok(new { id, name = $"Customer {id}" }));
        app.MapGet("/customers/{id:int}/slow", async (int id, CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            return Results.Ok(new { id });
        });
        app.Run();
        break;
    }

    default:
        Console.WriteLine("usage: secrets | token --signing-key <b64> | encrypt --key <b64> --client-id <op> --json <json> | mock-upstream [--urls <url>]");
        return 1;
}

return 0;
