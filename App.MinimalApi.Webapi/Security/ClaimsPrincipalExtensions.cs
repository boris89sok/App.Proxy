namespace App.MinimalApi.Webapi.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated identity produced by one scheme (API key or JWT) of the composite principal.</summary>
    public static ClaimsIdentity? GetIdentity(this ClaimsPrincipal user, string authenticationType) =>
        user.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == authenticationType);
}
