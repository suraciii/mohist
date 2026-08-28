using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.GitHub.Ports;

public sealed class GitHubAppOptions
{
    public const string SectionName = "Mohist:GitHub";

    public long AppId { get; set; }
    public string? AppSlug { get; set; }
    public string? PrivateKeyPath { get; set; }

    public bool IsConfigured => AppId > 0
        && !string.IsNullOrWhiteSpace(AppSlug)
        && !string.IsNullOrWhiteSpace(PrivateKeyPath);
}

public sealed record GitHubRepositoryInstallation(
    string InstallationId,
    string Owner,
    string Repo,
    string RepositoryNodeId);

public sealed record GitHubInstallationToken(string AccessToken, DateTimeOffset ExpiresAt);

public interface IGitHubAppClient
{
    Task<GitHubRepositoryInstallation> DiscoverInstallationAsync(string owner, string repo, CancellationToken ct = default);

    Task<GitHubInstallationToken> CreateInstallationTokenAsync(string installationId, CancellationToken ct = default);
}

public sealed class GitHubAppNotConfiguredException : Exception
{
    public GitHubAppNotConfiguredException()
        : base("GitHub App identity is not configured on this Server.")
    {
    }
}

public sealed class GitHubAppInstallationException : GitHubRemoteRequestException
{
    public GitHubAppInstallationException(
        string message,
        string code,
        object? details = null,
        HttpStatusCode? statusCode = null,
        bool isRateLimited = false,
        Exception? inner = null)
        : base(message, statusCode, isRateLimited, inner)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }
    public object? Details { get; }
}

public sealed class GitHubAppClient : IGitHubAppClient
{
    private readonly HttpClient _http;
    private readonly IOptions<GitHubAppOptions> _options;
    private readonly ISecretKeyFileOperations _files;
    private readonly TimeProvider _time;

    public GitHubAppClient(
        HttpClient http,
        IOptions<GitHubAppOptions> options,
        ISecretKeyFileOperations files,
        TimeProvider time)
    {
        _http = http;
        _options = options;
        _files = files;
        _time = time;
    }

    public async Task<GitHubRepositoryInstallation> DiscoverInstallationAsync(
        string owner,
        string repo,
        CancellationToken ct = default)
    {
        var normalizedOwner = owner.Trim();
        var normalizedRepo = repo.Trim();
        var jwt = await CreateAppJwtAsync(ct);
        using var installation = await SendAsync(
            $"/repos/{Uri.EscapeDataString(normalizedOwner)}/{Uri.EscapeDataString(normalizedRepo)}/installation",
            HttpMethod.Get,
            jwt,
            ct);
        if (installation.StatusCode == HttpStatusCode.NotFound)
            throw InstallationRequired(normalizedOwner, normalizedRepo);
        if (!installation.IsSuccessStatusCode)
            throw await DiscoveryFailureAsync(installation, normalizedOwner, normalizedRepo, "github_app_installation_lookup_failed", ct);

        var installationNode = await ParseObjectAsync(installation, ct);
        var installationId = IdentifierValue(installationNode, "id");
        if (string.IsNullOrWhiteSpace(installationId))
            throw new GitHubAppInstallationException(
                "GitHub returned an installation without an id.",
                "github_app_installation_response_invalid");

        var token = await CreateInstallationTokenAsync(installationId, ct);
        using var repository = await SendAsync(
            $"/repos/{Uri.EscapeDataString(normalizedOwner)}/{Uri.EscapeDataString(normalizedRepo)}",
            HttpMethod.Get,
            token.AccessToken,
            ct);
        if (!repository.IsSuccessStatusCode)
        {
            throw await DiscoveryFailureAsync(repository, normalizedOwner, normalizedRepo, "github_app_repository_verification_failed", ct);
        }

        var repositoryNode = await ParseObjectAsync(repository, ct);
        var canonicalOwner = repositoryNode["owner"]?["login"]?.GetValue<string>() ?? normalizedOwner;
        var canonicalRepo = StringValue(repositoryNode, "name") ?? normalizedRepo;
        var nodeId = StringValue(repositoryNode, "node_id");
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new GitHubAppInstallationException(
                "GitHub returned a repository without a stable node id.",
                "github_app_repository_response_invalid");

        return new GitHubRepositoryInstallation(installationId, canonicalOwner, canonicalRepo, nodeId);
    }

    public async Task<GitHubInstallationToken> CreateInstallationTokenAsync(
        string installationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installationId))
            throw new ArgumentException("Installation id is required.", nameof(installationId));
        using var response = await SendAsync(
            $"/app/installations/{Uri.EscapeDataString(installationId)}/access_tokens",
            HttpMethod.Post,
            await CreateAppJwtAsync(ct),
            ct);
        if (!response.IsSuccessStatusCode)
            throw await RemoteFailureAsync(response, "github_app_token_exchange_failed", ct);

        var node = await ParseObjectAsync(response, ct);
        var token = StringValue(node, "token");
        var expires = StringValue(node, "expires_at");
        if (string.IsNullOrWhiteSpace(token) || !DateTimeOffset.TryParse(expires, out var expiresAt))
            throw new GitHubAppInstallationException(
                "GitHub returned an invalid installation token response.",
                "github_app_token_response_invalid");
        return new GitHubInstallationToken(token, expiresAt);
    }

    private async Task<string> CreateAppJwtAsync(CancellationToken ct)
    {
        var options = _options.Value;
        if (!options.IsConfigured)
            throw new GitHubAppNotConfiguredException();

        var path = options.PrivateKeyPath!;
        if (!_files.FileExists(path))
            throw new GitHubAppInstallationException(
                "The configured GitHub App private key was not found. Configure a regular owner-only key file.",
                "github_app_private_key_missing");
        try
        {
            if (_files.IsReparsePoint(path))
                throw new GitHubAppInstallationException(
                    "The configured GitHub App private key must not be a symbolic link.",
                    "github_app_private_key_symlink");
            if (!OperatingSystem.IsWindows())
            {
                var mode = _files.GetUnixFileMode(path);
                const UnixFileMode forbidden = UnixFileMode.OtherRead
                    | UnixFileMode.OtherWrite
                    | UnixFileMode.OtherExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute;
                if ((mode & forbidden) != 0)
                    throw new GitHubAppInstallationException(
                        "The configured GitHub App private key must be readable only by its owner (mode 0600).",
                        "github_app_private_key_permissions");
            }

            var pem = Encoding.UTF8.GetString(await _files.ReadAllBytesAsync(path, ct));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);

            var now = _time.GetUtcNow();
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
                exp = now.AddMinutes(9).ToUnixTimeSeconds(),
                iss = options.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            var unsigned = Encoding.UTF8.GetBytes($"{header}.{payload}");
            var signature = rsa.SignData(unsigned, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{header}.{payload}.{Base64Url(signature)}";
        }
        catch (GitHubAppInstallationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new GitHubAppInstallationException(
                "The configured GitHub App private key is invalid.",
                "github_app_private_key_invalid",
                inner: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GitHubAppInstallationException(
                "The configured GitHub App private key could not be read. Check the path and owner-only permissions.",
                "github_app_private_key_unreadable",
                inner: ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, HttpMethod method, string bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<JsonObject> ParseObjectAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text)?.AsObject()
                ?? throw new JsonException("Expected JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new GitHubAppInstallationException(
                "GitHub returned malformed JSON.",
                "github_app_response_invalid",
                inner: ex);
        }
    }

    private async Task<GitHubAppInstallationException> DiscoveryFailureAsync(
        HttpResponseMessage response,
        string owner,
        string repo,
        string fallbackCode,
        CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return InstallationRequired(owner, repo);
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new GitHubAppInstallationException(
                "The GitHub App credentials were rejected. Check the App ID and private key.",
                "github_app_credential_rejected",
                new { status = (int)response.StatusCode, detail },
                response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var rateLimited = IsRateLimited(response);
            return new GitHubAppInstallationException(
                rateLimited
                    ? "GitHub rate limited App discovery. Wait and retry."
                    : "The GitHub App cannot access this Repository. Update the App Repository scope, then retry.",
                rateLimited ? "github_app_rate_limited" : "github_app_permission_denied",
                new { status = (int)response.StatusCode, detail },
                response.StatusCode,
                rateLimited);
        }
        return new GitHubAppInstallationException(
            $"GitHub App request failed with status {(int)response.StatusCode}.",
            fallbackCode,
            new { status = (int)response.StatusCode, detail },
            response.StatusCode);
    }

    private static async Task<GitHubAppInstallationException> RemoteFailureAsync(
        HttpResponseMessage response,
        string code,
        CancellationToken ct)
    {
        var detail = await response.Content.ReadAsStringAsync(ct);
        var status = response.StatusCode;
        var rateLimited = status == HttpStatusCode.TooManyRequests
            || status == HttpStatusCode.Forbidden && IsRateLimited(response);
        var credentialRejected = status == HttpStatusCode.Unauthorized;
        var permissionDenied = status == HttpStatusCode.Forbidden && !rateLimited;
        return new GitHubAppInstallationException(
            credentialRejected
                ? "The GitHub App credentials were rejected. Check the App ID and private key."
                : rateLimited
                    ? "GitHub rate limited the App request. Wait and retry."
                    : permissionDenied
                        ? "The GitHub App cannot access this Repository. Update the App Repository scope, then retry."
                        : $"GitHub App request failed with status {(int)status}.",
            credentialRejected
                ? "github_app_credential_rejected"
                : rateLimited
                    ? "github_app_rate_limited"
                    : permissionDenied ? "github_app_permission_denied" : code,
            new { status = (int)status, detail },
            status,
            rateLimited);
    }

    private GitHubAppInstallationException InstallationRequired(string owner, string repo) =>
        new(
            "The GitHub App is not installed for this Repository. Install it, then retry.",
            "github_app_installation_required",
            new
            {
                installationUrl = $"https://github.com/apps/{_options.Value.AppSlug}/installations/new",
                action = $"Install the App or add {owner}/{repo} to its scope, then retry.",
            });

    private static string? StringValue(JsonObject node, string key)
    {
        var value = node[key];
        if (value is null) return null;
        try
        {
            return value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? IdentifierValue(JsonObject node, string key)
    {
        var value = node[key];
        if (value is null) return null;
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => value.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var retryAfter)
            && retryAfter.Any(value =>
                int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
                || DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _)))
            return true;
        return response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.Any(value => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count) && count == 0);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public interface IGitHubInstallationTokenProvider
{
    Task<GitHubInstallationToken> GetAsync(string installationId, CancellationToken ct = default);
    void Invalidate(string installationId, string accessToken);
}

public sealed class GitHubInstallationTokenProvider : IGitHubInstallationTokenProvider
{
    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private readonly IGitHubAppClient _client;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly object _gatesLock = new();

    public GitHubInstallationTokenProvider(IGitHubAppClient client, TimeProvider time)
    {
        _client = client;
        _time = time;
    }

    public async Task<GitHubInstallationToken> GetAsync(string installationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(installationId, out var cached) && !IsStale(cached))
            return new GitHubInstallationToken(cached.AccessToken, cached.ExpiresAt);

        var gate = AddGateUser(installationId);
        try
        {
            await gate.Semaphore.WaitAsync(ct);
            try
            {
                if (_cache.TryGetValue(installationId, out cached) && !IsStale(cached))
                    return new GitHubInstallationToken(cached.AccessToken, cached.ExpiresAt);
                var fresh = await _client.CreateInstallationTokenAsync(installationId, ct);
                _cache[installationId] = new CachedToken(fresh.AccessToken, fresh.ExpiresAt);
                return fresh;
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            RemoveGateUser(installationId, gate);
        }
    }

    public void Invalidate(string installationId, string accessToken)
    {
        if (_cache.TryGetValue(installationId, out var cached)
            && string.Equals(cached.AccessToken, accessToken, StringComparison.Ordinal))
        {
            _cache.TryRemove(installationId, out _);
        }
    }

    private Gate AddGateUser(string installationId)
    {
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(installationId, out var gate))
            {
                gate = new Gate();
                _gates.Add(installationId, gate);
            }
            gate.Users++;
            return gate;
        }
    }

    private void RemoveGateUser(string installationId, Gate gate)
    {
        lock (_gatesLock)
        {
            gate.Users--;
            if (gate.Users == 0
                && _gates.TryGetValue(installationId, out var current)
                && ReferenceEquals(current, gate))
            {
                _gates.Remove(installationId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private bool IsStale(CachedToken token) => token.ExpiresAt <= _time.GetUtcNow().AddMinutes(1);
}
