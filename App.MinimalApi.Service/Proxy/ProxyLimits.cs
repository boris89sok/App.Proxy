namespace App.MinimalApi.Service.Proxy;

/// <summary>Hard limits enforced in code (they mirror the CHECK constraints in Database/*.sql).</summary>
public static class ProxyLimits
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 120;
    public const int MaxResponseBytes = 104_857_600;

    public static bool IsValidTimeout(int seconds) => seconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds;
}
