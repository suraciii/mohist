namespace Mohist.Server.Auth.Domain;

/// <summary>
/// A runner enrollment token is one-time and short-lived: 15 minutes
/// covers the install window, after which a fresh install must mint a
/// new token.
/// </summary>
public static class EnrollmentTokenPolicy
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
}
