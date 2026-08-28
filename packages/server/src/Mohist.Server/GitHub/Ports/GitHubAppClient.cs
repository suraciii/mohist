using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Mohist.Server.SystemInfo;

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

public sealed class GitHubAppInstallationException : Exception
{
    public GitHubAppInstallationException(string message, string code, object? details = null, Exception? inner = null)
        : base(message, inner)
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
    private readonly IFileSystem _files;
    private readonly TimeProvider _time;

    public GitHubAppClient(
        HttpClient http,
        IOptions<GitHubAppOptions> options,
        IFileSystem files,
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
        var jwt = CreateAppJwt();
        using var installation = await SendAsync(
            $"/repos/{Uri.EscapeDataString(normalizedOwner)}/{Uri.EscapeDataString(normalizedRepo)}/installation",
            HttpMethod.Get,
            jwt,
            ct);
        if (installation.StatusCode == HttpStatusCode.NotFound)
            throw InstallationRequired(normalizedOwner, normalizedRepo);
        if (!installation.IsSuccessStatusCode)
        {
            if (installation.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw InstallationRequired(normalizedOwner, normalizedRepo);
            throw await RemoteFailureAsync(installation, "github_app_installation_lookup_failed", ct);
        }

        var installationNode = await ParseObjectAsync(installation, ct);
        var installationId = StringValue(installationNode, "id");
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
            if (repository.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw InstallationRequired(normalizedOwner, normalizedRepo);
            throw await RemoteFailureAsync(repository, "github_app_repository_verification_failed", ct);
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
            CreateAppJwt(),
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

    private string CreateAppJwt()
    {
        var options = _options.Value;
        if (!options.IsConfigured)
            throw new GitHubAppNotConfiguredException();
        if (!_files.Exists(options.PrivateKeyPath!))
            throw new GitHubAppInstallationException(
                "The configured GitHub App private key was not found.",
                "github_app_private_key_missing");

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(_files.ReadAllText(options.PrivateKeyPath!));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new GitHubAppInstallationException(
                "The configured GitHub App private key is invalid.",
                "github_app_private_key_invalid",
                inner: ex);
        }

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

    private static async Task<GitHubAppInstallationException> RemoteFailureAsync(
        HttpResponseMessage response,
        string code,
        CancellationToken ct)
    {
        var detail = await response.Content.ReadAsStringAsync(ct);
        var message = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            ? "The GitHub App cannot access this Repository. Install the App or update its Repository scope, then retry."
            : $"GitHub App request failed with status {(int)response.StatusCode}.";
        return new GitHubAppInstallationException(message, code, new { status = (int)response.StatusCode, detail });
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

    private static string? StringValue(JsonObject node, string key) =>
        node[key]?.GetValue<string>();

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

    private readonly IGitHubAppClient _client;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public GitHubInstallationTokenProvider(IGitHubAppClient client, TimeProvider time)
    {
        _client = client;
        _time = time;
    }

    public async Task<GitHubInstallationToken> GetAsync(string installationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(installationId, out var cached) && !IsStale(cached))
            return new GitHubInstallationToken(cached.AccessToken, cached.ExpiresAt);

        var gate = _gates.GetOrAdd(installationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
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
            gate.Release();
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

    private bool IsStale(CachedToken token) => token.ExpiresAt <= _time.GetUtcNow().AddMinutes(1);
}
