namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// Predicate shared by the secret store's <see cref="ISecretStore.Redact"/>
/// helper and any log-line / config-read surface that must scrub a
/// connection-supplied credential. Matches the policy already used by
/// <c>ConfigRoutes.IsSecretKey</c> so a field whose name contains
/// <c>token</c>, <c>secret</c>, or <c>key</c> is redacted. Centralising
/// the predicate keeps every surface in lockstep — adding a new
/// sensitive-name fragment only requires editing one location.
/// </summary>
public static class SecretNameDetector
{
    public static bool IsSecretKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        return key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase);
    }
}
