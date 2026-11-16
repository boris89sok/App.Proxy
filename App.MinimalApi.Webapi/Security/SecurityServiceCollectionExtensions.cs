namespace App.MinimalApi.Webapi.Security;

public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Proxy endpoints require BOTH an API key (which application is calling) and a JWT bearer token
    /// (which user/service identity, and roles). Either one alone is rejected with 401.
    /// </summary>
    public static IServiceCollection AddProxySecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<SecuritySettings>(configuration.GetSection(SecuritySettings.SectionName));

        services.AddAuthentication(CompositeAuthenticationHandler.SchemeName)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(SecurityPolicies.ApiKeyScheme, null)
            .AddScheme<AuthenticationSchemeOptions, CompositeAuthenticationHandler>(CompositeAuthenticationHandler.SchemeName, null);

        // Configured lazily from validated settings (secrets are resolved at runtime, not at registration).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<SecuritySettings>>((jwt, settings) =>
            {
                var s = settings.Value.Jwt;
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = s.Issuer,
                    ValidateAudience = true,
                    ValidAudience = s.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(s.GetSigningKeyBytes() ?? new byte[32]),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    AuthenticationType = SecurityPolicies.JwtAuthenticationType
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(SecurityPolicies.ProxyClient, policy => policy
                .AddAuthenticationSchemes(CompositeAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasBothIdentities(ctx.User)));
        });

        return services;
    }

    private static ClaimsIdentity? JwtIdentity(ClaimsPrincipal user) => user.GetIdentity(SecurityPolicies.JwtAuthenticationType);

    private static bool HasBothIdentities(ClaimsPrincipal user) =>
        JwtIdentity(user) is not null && user.GetIdentity(SecurityPolicies.ApiKeyScheme) is not null;
}
