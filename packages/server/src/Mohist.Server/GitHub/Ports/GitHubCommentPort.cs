using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.GitHub.Ports;

/// <summary>
/// Production comment port: talks to the GitHub REST issues endpoints with
/// the connection's fine-grained PAT (<c>:api</c> secret, Issues read/write
/// only). App-identity connections are not yet supported: the
/// installation-token exchange is delivered with the full write-back
/// writer. The caller treats failures as best-effort, so a
/// not-yet-supported identity never blocks event processing.
/// </summary>
public sealed class GitHubCommentPort : IGitHubCommentPort
{
    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private readonly ILogger<GitHubCommentPort> _log;

    public GitHubCommentPort(
        HttpClient http,
        GitHubConnectionStore connections,
        ISecretStore secrets,
        ILogger<GitHubCommentPort> log)
    {
        _http = http;
        _secrets = secrets;
        _log = log;
    }

    public Task PostCommentAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var url = $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}/comments";
        return SendAsync(connection, url, HttpMethod.Post, JsonContent.Create(new JsonObject { ["body"] = body }), ct);
    }

    public async Task ReplaceStateLabelAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string stateLabel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var url = $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}";
        var pat = await LoadPatAsync(connection, ct);
        using var getRequest = BuildRequest(url, HttpMethod.Get, content: null, pat);
        using var getResponse = await _http.SendAsync(getRequest, ct);
        if (!getResponse.IsSuccessStatusCode)
        {
            await LogFailureAsync(getResponse, url, ct);
            getResponse.EnsureSuccessStatusCode();
        }

        var node = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync(ct));
        var names = new List<string>();
        if (node?["labels"]?.AsArray() is { } labels)
        {
            foreach (var label in labels)
            {
                var name = label?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name)
                    && !name.StartsWith("mohist:", StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }
        }
        if (!names.Contains(stateLabel, StringComparer.Ordinal))
            names.Add(stateLabel);

        var patchBody = new JsonObject
        {
            ["labels"] = new JsonArray(names.Select(n => JsonValue.Create(n)).ToArray()),
        };
        await SendAsync(connection, url, HttpMethod.Patch, JsonContent.Create(patchBody), ct);
    }

    public Task CloseIssueAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string stateReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var url = $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}";
        var body = new JsonObject
        {
            ["state"] = "closed",
            ["state_reason"] = stateReason,
        };
        return SendAsync(connection, url, HttpMethod.Patch, JsonContent.Create(body), ct);
    }

    private async Task SendAsync(
        GitHubConnection connection,
        string url,
        HttpMethod method,
        HttpContent? content,
        CancellationToken ct)
    {
        var pat = await LoadPatAsync(connection, ct);
        using var request = BuildRequest(url, method, content, pat);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync(response, url, ct);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<byte[]> LoadPatAsync(GitHubConnection connection, CancellationToken ct)
    {
        if (connection.IdentityKind != GitHubIdentityKind.Pat)
            throw new NotSupportedException(
                $"GitHub comment write-back for identity kind '{connection.IdentityKind}' is not supported yet (GitHub App installation tokens arrive with the full write-back writer)");

        var pat = await _secrets.LoadAsync(
            GitHubConnectionStore.ApiSecretAddress(connection.ProjectId, connection.Id), ct);
        if (pat is null || pat.Length == 0)
            throw new InvalidOperationException(
                $"GitHub connection '{connection.Id}' has no ':api' PAT secret configured");
        return pat;
    }

    private static HttpRequestMessage BuildRequest(string url, HttpMethod method, HttpContent? content, byte[] pat)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new("Bearer", Encoding.UTF8.GetString(pat));
        return request;
    }

    private async Task LogFailureAsync(HttpResponseMessage response, string url, CancellationToken ct)
    {
        var detail = await response.Content.ReadAsStringAsync(ct);
        _log.LogWarning(
            "GitHub write-back request to {Url} failed with {Status}: {Detail}",
            url, (int)response.StatusCode, detail);
    }
}
