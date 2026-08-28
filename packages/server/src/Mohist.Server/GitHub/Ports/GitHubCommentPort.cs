using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;

namespace Mohist.Server.GitHub.Ports;

/// <summary>
/// Production GitHub REST adapter. Every outbound request uses the verified
/// connection installation token. The token provider owns JWT exchange and
/// cache lifetime; this adapter owns one-request authentication recovery.
/// </summary>
public sealed class GitHubCommentPort : IGitHubCommentPort, IGitHubIssuePort
{
    private readonly HttpClient _http;
    private readonly IGitHubInstallationTokenProvider _tokens;
    private readonly GitHubConnectionStore? _connections;
    private readonly ILogger<GitHubCommentPort> _log;

    public GitHubCommentPort(
        HttpClient http,
        IGitHubInstallationTokenProvider tokens,
        GitHubConnectionStore? connections,
        ILogger<GitHubCommentPort> log)
    {
        _http = http;
        _tokens = tokens;
        _connections = connections;
        _log = log;
    }

    public async Task<int> CreateIssueAsync(GitHubConnection connection, string title, string body, string marker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var response = await SendAsync(connection, $"/repos/{connection.Owner}/{connection.Repo}/issues", HttpMethod.Post,
            () => JsonContent.Create(new JsonObject { ["title"] = title, ["body"] = GitHubMirrorMarker.Append(body, marker) }), ct);
        var text = await ReadSuccessAsync(response, ct);
        try
        {
            var number = JsonNode.Parse(text)?["number"]?.GetValue<int>();
            if (number is not > 0)
                throw new InvalidOperationException("GitHub create issue response did not contain a valid number");
            return number.Value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new GitHubRemoteOutcomeUnknownException("GitHub create issue returned a successful but unusable response", ex);
        }
    }

    public async Task<int?> FindIssueByMarkerAsync(GitHubConnection connection, string marker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        const int pageSize = 100;
        var matches = new List<int>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(connection,
                $"/repos/{connection.Owner}/{connection.Repo}/issues?state=all&per_page={pageSize}&page={page}",
                HttpMethod.Get, static () => null, ct);
            var items = JsonNode.Parse(await ReadSuccessAsync(response, ct))?.AsArray();
            if (items is null || items.Count == 0)
                break;
            foreach (var item in items)
            {
                if (item is not JsonObject issue || issue.ContainsKey("pull_request"))
                    continue;
                if (issue["body"]?.GetValue<string>()?.Contains(marker, StringComparison.Ordinal) != true)
                    continue;
                var number = issue["number"]?.GetValue<int>();
                if (number is not > 0)
                    throw new InvalidOperationException("GitHub mirror marker matched an issue without a valid number");
                matches.Add(number.Value);
                if (matches.Count > 1)
                    throw new InvalidOperationException("GitHub mirror marker matched multiple issues; reconciliation is ambiguous");
            }
            if (items.Count < pageSize)
                break;
        }
        return matches.Count == 0 ? null : matches[0];
    }

    public async Task<GitHubIssueSnapshot?> GetIssueAsync(GitHubConnection connection, int githubIssueNumber, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var response = await SendAsync(connection, $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}", HttpMethod.Get, static () => null, ct);
        var node = JsonNode.Parse(await ReadSuccessAsync(response, ct));
        var number = node?["number"]?.GetValue<int>();
        var title = node?["title"]?.GetValue<string>();
        if (number is not > 0 || title is null)
            throw new InvalidOperationException("GitHub issue response did not contain number and title");
        return new GitHubIssueSnapshot(number.Value, title, node?["body"]?.GetValue<string>(), node?["state"]?.GetValue<string>(), node?["state_reason"]?.GetValue<string>());
    }

    public async Task UpdateIssueAsync(GitHubConnection connection, int githubIssueNumber, string title, string body, string marker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var response = await SendAsync(connection, $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}", HttpMethod.Patch,
            () => JsonContent.Create(new JsonObject { ["title"] = title, ["body"] = GitHubMirrorMarker.Append(body, marker) }), ct);
        _ = await ReadSuccessAsync(response, ct);
    }

    public async Task PostCommentAsync(GitHubConnection connection, int githubIssueNumber, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var response = await SendAsync(connection, $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}/comments", HttpMethod.Post,
            () => JsonContent.Create(new JsonObject { ["body"] = body }), ct);
        _ = await ReadSuccessAsync(response, ct);
    }

    public async Task<IReadOnlyList<string>> FindCommentIdsByMarkerAsync(GitHubConnection connection, int githubIssueNumber, string marker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        const int pageSize = 100;
        var matches = new List<string>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(connection,
                $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}/comments?per_page={pageSize}&page={page}",
                HttpMethod.Get, static () => null, ct);
            var items = JsonNode.Parse(await ReadSuccessAsync(response, ct))?.AsArray();
            if (items is null || items.Count == 0)
                break;
            foreach (var item in items)
            {
                if (item?["body"]?.GetValue<string>()?.Contains(marker, StringComparison.Ordinal) != true)
                    continue;
                var idNode = item["id"];
                var id = idNode?.GetValueKind() switch
                {
                    JsonValueKind.String => idNode.GetValue<string>(),
                    JsonValueKind.Number => idNode.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("GitHub comment marker matched a comment without a valid id");
                matches.Add(id);
            }
            if (items.Count < pageSize)
                break;
        }
        return matches;
    }

    public async Task ReplaceStateLabelAsync(GitHubConnection connection, int githubIssueNumber, string stateLabel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var url = $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}";
        using var get = await SendAsync(connection, url, HttpMethod.Get, static () => null, ct);
        var node = JsonNode.Parse(await ReadSuccessAsync(get, ct));
        var names = new List<string>();
        if (node?["labels"]?.AsArray() is { } labels)
        {
            foreach (var label in labels)
            {
                var name = label?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("mohist:", StringComparison.Ordinal))
                    names.Add(name);
            }
        }
        if (!names.Contains(stateLabel, StringComparer.Ordinal))
            names.Add(stateLabel);
        using var patch = await SendAsync(connection, url, HttpMethod.Patch,
            () => JsonContent.Create(new JsonObject { ["labels"] = new JsonArray(names.Select(name => JsonValue.Create(name)).ToArray()) }), ct);
        _ = await ReadSuccessAsync(patch, ct);
    }

    public async Task CloseIssueAsync(GitHubConnection connection, int githubIssueNumber, string stateReason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var response = await SendAsync(connection, $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}", HttpMethod.Patch,
            () => JsonContent.Create(new JsonObject { ["state"] = "closed", ["state_reason"] = stateReason }), ct);
        _ = await ReadSuccessAsync(response, ct);
    }

    public async Task<string?> FindDeliveryPullRequestUrlAsync(GitHubConnection connection, int issueNumber, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var response = await SendAsync(connection,
            $"/repos/{connection.Owner}/{connection.Repo}/pulls?head={connection.Owner}:mo/issue-{issueNumber}&state=all",
            HttpMethod.Get, static () => null, ct);
        var array = JsonNode.Parse(await ReadSuccessAsync(response, ct)) as JsonArray;
        return array?.FirstOrDefault()?["html_url"]?.GetValue<string>();
    }

    private async Task<HttpResponseMessage> SendAsync(
        GitHubConnection connection,
        string url,
        HttpMethod method,
        Func<HttpContent?> contentFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.InstallationId))
            throw new InvalidOperationException($"GitHub connection '{connection.Id}' has no installation identity");

        var token = await GetTokenAsync(connection, ct);
        var response = await SendOnceAsync(url, method, contentFactory, token.AccessToken, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return await HandleAuthFailureAsync(connection, url, response, ct);

        response.Dispose();
        _tokens.Invalidate(connection.InstallationId, token.AccessToken);
        var refreshed = await GetTokenAsync(connection, ct);
        response = await SendOnceAsync(url, method, contentFactory, refreshed.AccessToken, ct);
        return await HandleAuthFailureAsync(connection, url, response, ct);
    }

    private async Task<GitHubInstallationToken> GetTokenAsync(
        GitHubConnection connection,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.InstallationId))
            throw new InvalidOperationException($"GitHub connection '{connection.Id}' has no installation identity");

        try
        {
            return await _tokens.GetAsync(connection.InstallationId, ct);
        }
        catch (GitHubRemoteRequestException exception)
            when (IsCredentialFailure(exception) || IsInstallationUnavailable(exception))
        {
            await MarkInstallationUnavailableAsync(connection, exception, ct);
            throw;
        }
    }

    private async Task<HttpResponseMessage> HandleAuthFailureAsync(
        GitHubConnection connection,
        string url,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return response;
        var detail = await response.Content.ReadAsStringAsync(ct);
        _log.LogWarning("GitHub request to {Url} failed with {Status}: {Detail}", url, (int)response.StatusCode, detail);
        var rateLimited = IsRateLimited(response);
        var status = response.StatusCode;
        if (IsCredentialFailure(status, rateLimited))
            await MarkInstallationUnavailableAsync(connection, status, ct);
        response.Dispose();
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || rateLimited)
            throw new GitHubRemoteRequestException(
                $"GitHub request failed with status {(int)status}.",
                status,
                rateLimited);
        throw new HttpRequestException($"GitHub request failed with status {(int)status}.", null, status);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(string url, HttpMethod method, Func<HttpContent?> contentFactory, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = contentFactory() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task MarkInstallationUnavailableAsync(
        GitHubConnection connection,
        GitHubRemoteRequestException exception,
        CancellationToken ct)
    {
        if (_connections is not null)
        {
            try
            {
                var (code, detail) = ClassifyInstallationFailure(exception);
                await _connections.MarkInstallationUnavailableAsync(
                    connection.ProjectId,
                    connection.Id,
                    code,
                    detail,
                    ct);
            }
            catch (Exception markException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(markException,
                    "Could not persist GitHub App installation attention for connection {ConnectionId}",
                    connection.Id);
            }
        }
    }

    private Task MarkInstallationUnavailableAsync(
        GitHubConnection connection,
        HttpStatusCode status,
        CancellationToken ct) =>
        MarkInstallationUnavailableAsync(
            connection,
            new GitHubRemoteRequestException(
                $"GitHub App request failed with status {(int)status}.",
                status),
            ct);

    private static (string Code, string Detail) ClassifyInstallationFailure(
        GitHubRemoteRequestException exception)
    {
        if (exception is GitHubAppInstallationException appException)
        {
            var detail = appException.Details is null
                ? appException.Message
                : $"{appException.Message} {JsonSerializer.Serialize(appException.Details)}";
            return appException.Code switch
            {
                "github_app_installation_required" => ("github_app_installation_required", detail),
                "github_app_credential_rejected" => ("github_app_token_rejected", detail),
                "github_app_permission_denied" => ("github_app_permission_denied", detail),
                _ => (CodeForStatus(exception.StatusCode), detail),
            };
        }

        return (
            CodeForStatus(exception.StatusCode),
            exception.StatusCode == HttpStatusCode.Unauthorized
                ? "The GitHub App token was rejected. Check the App credentials and installation scope, then reconnect."
                : "The GitHub App installation cannot access this Repository. Repair the installation scope, then reconnect.");
    }

    private static string CodeForStatus(HttpStatusCode? status) =>
        status == HttpStatusCode.Unauthorized
            ? "github_app_token_rejected"
            : "github_app_permission_denied";

    private static bool IsCredentialFailure(GitHubRemoteRequestException exception) =>
        IsCredentialFailure(exception.StatusCode, exception.IsRateLimited);

    private static bool IsInstallationUnavailable(GitHubRemoteRequestException exception) =>
        exception is GitHubAppInstallationException
        {
            Code: "github_app_installation_required"
        };

    private static bool IsCredentialFailure(HttpStatusCode? status, bool isRateLimited) =>
        status == HttpStatusCode.Unauthorized
        || status == HttpStatusCode.Forbidden && !isRateLimited;

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

    private static async Task<string> ReadSuccessAsync(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadAsStringAsync(ct);
}
