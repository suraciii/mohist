using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.Auth.Domain;

/// <summary>
/// Shape of issued credentials: <c>moh_&lt;kind&gt;_&lt;base64url(32B)&gt;</c>.
/// The kind prefix keeps tokens human-recognizable and leak-scan friendly;
/// only the SHA-256 hash of the full token is ever persisted.
/// </summary>
public static class CredentialToken
{
    private const int RandomByteCount = 32;

    public static string Generate(CredentialKind kind) =>
        $"moh_{KindName(kind)}_{NewSecret()}";

    /// <summary>
    /// Generates a one-time runner enrollment token. Deliberately not
    /// parseable as a credential (<see cref="TryParse"/> only knows
    /// credential kinds), so it can never be presented as a Bearer
    /// credential — the register endpoint consumes it from the body.
    /// </summary>
    public static string GenerateEnrollmentToken() => $"moh_enroll_{NewSecret()}";

    /// <summary>
    /// Generates a one-time RFC 8628 device code. Not parseable as a
    /// credential (<see cref="TryParse"/> only knows credential kinds),
    /// so it can never be presented as a Bearer credential — the token
    /// endpoint consumes it from the body.
    /// </summary>
    public static string GenerateDeviceCode() => $"moh_device_{NewSecret()}";

    private static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(RandomByteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// True when <paramref name="token"/> has the issued shape with a
    /// known kind and a non-empty secret. The secret itself is not
    /// validated further: lookup hashes the full token, so any garbage
    /// secret simply misses.
    /// </summary>
    public static bool TryParse(string token, out CredentialKind kind)
    {
        kind = default;
        if (string.IsNullOrEmpty(token))
            return false;

        // The secret is base64url and may itself contain '_', so only the
        // first two separators delimit the shape.
        var firstSeparator = token.IndexOf('_');
        if (firstSeparator <= 0)
            return false;
        if (!string.Equals(token[..firstSeparator], "moh", StringComparison.Ordinal))
            return false;

        var secondSeparator = token.IndexOf('_', firstSeparator + 1);
        if (secondSeparator <= firstSeparator + 1)
            return false;
        if (token.Length <= secondSeparator + 1)
            return false;

        var kindName = token[(firstSeparator + 1)..secondSeparator];
        foreach (var candidate in Enum.GetValues<CredentialKind>())
        {
            if (string.Equals(kindName, KindName(candidate), StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        return false;
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// A short display prefix for listing: kind prefix plus the first few
    /// secret characters, so a holder can match a token they have to a
    /// list entry without revealing enough of the secret to be useful
    /// (same practice as GitHub's token list).
    /// </summary>
    public const int DisplayPrefixSecretChars = 8;

    public static string DisplayPrefix(string token)
    {
        if (!TryParse(token, out _))
            return token;

        var secretStart = token.IndexOf('_', token.IndexOf('_') + 1) + 1;
        var length = Math.Min(DisplayPrefixSecretChars, token.Length - secretStart);
        return token[..(secretStart + length)];
    }

    private static string KindName(CredentialKind kind) => kind switch
    {
        CredentialKind.Session => "session",
        CredentialKind.Refresh => "refresh",
        CredentialKind.Pat => "pat",
        CredentialKind.Runner => "runner",
        CredentialKind.Integration => "integration",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
