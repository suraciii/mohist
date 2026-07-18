using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.Project.Domain;

/// <summary>
/// Credential-free Git remote URL normalizer and fingerprinter.
/// <para>
/// The versioned normalization is the single source of truth for "two
/// remote URLs refer to the same physical repository" across the
/// Server. Issue-backed runs persist a fingerprint computed by this
/// helper so the Runner can prove the workspace remote matches the
/// Server's authoritative declaration, and the
/// <c>RepositoryPolicy</c> alias-rejection check uses the same
/// fingerprint to refuse two Project-local resource names that resolve
/// to the same physical remote.
/// </para>
/// <para>
/// The normalizer is intentionally credential-free: it never retains
/// or emits any part of the userinfo segment of a URL, so the
/// fingerprint can safely travel through persisted Issue/Workflow
/// state and through the Runner workspace marker on disk without
/// exposing credentials. Conformance vectors are pinned by
/// <c>GitRemoteUrlNormalizerTests</c>; bumping
/// <see cref="NormalizationVersion"/> requires extending the test
/// fixture's expected output and is treated as a breaking change for
/// any persisted fingerprint.
/// </para>
/// </summary>
public static class GitRemoteUrlNormalizer
{
    /// <summary>
    /// Bump when the canonicalization rules change. Persisted
    /// fingerprints always carry this version, so a stale server can
    /// detect that a Runner/operator is mixing versions and refuse the
    /// comparison rather than silently agreeing.
    /// </summary>
    public const string NormalizationVersion = "git-remote-url/v1";

    /// <summary>
    /// Canonicalize <paramref name="rawUrl"/> into a stable string and
    /// return its lowercase SHA-256 fingerprint alongside the version
    /// stamp. Returns <c>null</c> when the URL cannot be canonicalized
    /// (empty, null, or unparseable); callers must fail closed and
    /// surface an actionable identity error rather than retrying with a
    /// synthesized default.
    /// </summary>
    public static GitRemoteFingerprint? Fingerprint(string? rawUrl)
    {
        if (TryNormalize(rawUrl, out var canonical))
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            var hex = Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);
            return new GitRemoteFingerprint(NormalizationVersion, hex, canonical);
        }
        return null;
    }

    /// <summary>
    /// Public helper for callers that want only the canonical string
    /// (e.g. error messages). Returns false on unparseable input.
    /// </summary>
    public static bool TryNormalize(string? rawUrl, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;

        var trimmed = rawUrl.Trim();

        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            var scheme = trimmed[..(schemeEnd + 3)].ToLowerInvariant();
            var remainder = trimmed[(schemeEnd + 3)..];
            return TryParseAuthorityAndPath(scheme, remainder, out canonical);
        }

        if (trimmed.StartsWith("git@", StringComparison.Ordinal))
        {
            return TryParseScpLike("ssh://", trimmed["git@".Length..], out canonical);
        }

        if (trimmed.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseScpLike("ssh://", trimmed["ssh:".Length..], out canonical);
        }

        var atIndex = trimmed.IndexOf('@');
        if (atIndex > 0)
        {
            var afterAt = trimmed[(atIndex + 1)..];
            var colonIndex = afterAt.IndexOf(':');
            var slashIndex = afterAt.IndexOf('/');
            if (colonIndex > 0 && (slashIndex < 0 || colonIndex < slashIndex))
            {
                return TryParseScpLike("ssh://", afterAt, out canonical);
            }
        }

        return false;
    }

    private static bool TryParseScpLike(string scheme, string body, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(body)) return false;

        var firstColon = body.IndexOf(':');
        var firstSlash = body.IndexOf('/');
        if (firstColon < 0) return false;
        if (firstSlash >= 0 && firstSlash < firstColon) return false;

        var host = body[..firstColon];
        var atIndex = host.LastIndexOf('@');
        if (atIndex >= 0)
        {
            host = host[(atIndex + 1)..];
        }
        var path = firstColon == body.Length - 1 ? string.Empty : body[(firstColon + 1)..];
        return TryCompose(scheme, host, path, out canonical);
    }

    private static bool TryParseAuthorityAndPath(string scheme, string remainder, out string canonical)
    {
        canonical = string.Empty;

        var atIndex = remainder.IndexOf('@');
        string hostPart;
        if (atIndex >= 0)
        {
            // Drop the userinfo segment (any combination of user /
            // user:password / token / oauth2 style). The fingerprint
            // must be credential-free, so anything before the @ is
            // discarded without validation.
            hostPart = remainder[(atIndex + 1)..];
        }
        else
        {
            hostPart = remainder;
        }

        var slashIndex = hostPart.IndexOf('/');
        if (slashIndex < 0)
        {
            return TryCompose(scheme, hostPart, string.Empty, out canonical);
        }
        var pathPart = hostPart[slashIndex..];
        hostPart = hostPart[..slashIndex];
        return TryCompose(scheme, hostPart, pathPart, out canonical);
    }

    private static bool TryCompose(string scheme, string rawHost, string rawPath, out string canonical)
    {
        canonical = string.Empty;
        var host = StripBrackets(rawHost);
        if (string.IsNullOrEmpty(host)) return false;

        // Drop default ports for known schemes. This keeps ":443" on
        // https and ":22" on ssh from breaking equivalence with their
        // unadorned counterparts. The remaining `:NNNN` forms are kept
        // verbatim (e.g. https://example.com:8443/...).
        host = StripDefaultPort(host, scheme);

        host = host.ToLowerInvariant();

        var path = rawPath ?? string.Empty;
        if (path.Length > 0)
        {
            var sb = new StringBuilder(path.Length);
            foreach (var raw in path)
            {
                if (raw == '?' || raw == '#') break;
                sb.Append(raw);
            }
            path = sb.ToString();
            while (path.EndsWith('/', StringComparison.Ordinal) && path.Length > 1)
            {
                path = path[..^1];
            }
            if (path.Length > 1 && path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }
            if (path.Length == 0 || path[0] != '/')
            {
                path = "/" + path;
            }
        }
        else
        {
            path = string.Empty;
        }

        canonical = scheme + host + path;
        return true;
    }

    private static string StripDefaultPort(string host, string scheme)
    {
        var colon = host.IndexOf(':');
        if (colon < 0) return host;
        var portPart = host[(colon + 1)..];
        if (string.IsNullOrEmpty(portPart)) return host[..colon];
        if (scheme == "https://" && portPart == "443") return host[..colon];
        if (scheme == "http://" && portPart == "80") return host[..colon];
        if (scheme == "ssh://" && portPart == "22") return host[..colon];
        return host;
    }

    private static string StripBrackets(string value)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
            return value[1..^1];
        return value;
    }
}

/// <summary>
/// The versioned fingerprint of a normalized Git remote URL. Carrying
/// the version alongside the digest lets a future bump distinguish "no
/// fingerprint yet" from "stale fingerprint produced by an old
/// normalizer" without coordinating a schema migration.
/// </summary>
public sealed record GitRemoteFingerprint(
    [property: System.Text.Json.Serialization.JsonPropertyName("version")] string Version,
    [property: System.Text.Json.Serialization.JsonPropertyName("fingerprint")] string Fingerprint,
    [property: System.Text.Json.Serialization.JsonPropertyName("canonical")] string Canonical);
