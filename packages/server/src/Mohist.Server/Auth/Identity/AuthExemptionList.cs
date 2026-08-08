using Microsoft.AspNetCore.Http;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// The closed set of endpoints reachable without authentication on the
/// auth surface. Everything else under /api, /hubs and /otel/api requires
/// a credential.
/// </summary>
public static class AuthExemptionList
{
    public static bool IsExempt(PathString path, string method)
    {
        if (string.Equals(method, HttpMethods.Get, StringComparison.Ordinal)
            && IsExactPath(path, "/api/health"))
            return true;

        if (!string.Equals(method, HttpMethods.Post, StringComparison.Ordinal))
            return false;

        if (IsExactPath(path, "/api/auth/session")
            || IsExactPath(path, "/api/auth/device/code")
            || IsExactPath(path, "/api/auth/token")
            || IsExactPath(path, "/api/runners/register"))
            return true;

        return IsGitHubIngress(path);
    }

    private static bool IsGitHubIngress(PathString path)
    {
        if (!path.HasValue)
            return false;
        var segments = path.Value.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4
            && string.Equals(segments[0], "api", StringComparison.Ordinal)
            && string.Equals(segments[1], "github-connections", StringComparison.Ordinal)
            && string.Equals(segments[3], "ingress", StringComparison.Ordinal);
    }

    private static bool IsExactPath(PathString path, string expected)
    {
        if (!path.HasValue)
            return false;
        var normalized = path.Value.EndsWith('/') ? path.Value[..^1] : path.Value;
        return string.Equals(normalized, expected, StringComparison.Ordinal);
    }
}
