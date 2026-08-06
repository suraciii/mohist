using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.GitHub.Ports;

/// <summary>
/// Production comment port: POSTs to the GitHub REST issues comments
/// endpoint with the connection's fine-grained PAT (<c>:api</c> secret,
/// Issues read/write only). App-identity connections are not yet supported:
/// the installation-token exchange is delivered with the full write-back
/// writer. The caller treats failures as best-effort, so a
/// not-yet-supported identity never blocks event processing.
/// </summary>
public sealed class GitHubCommentPort : IGitHubCommentPort
{
    private readonly HttpClient _http;
    private readonly GitHubConnectionStore _connections;
    private readonly ISecretStore _secrets;
    private readonly ILogger<GitHubCommentPort> _log;

    public GitHubCommentPort(
        HttpClient http,
        GitHubConnectionStore connections,
        ISecretStore secrets,
        ILogger<GitHubCommentPort> log)
    {
        _http = http;
        _connections = connections;
        _secrets = secrets;
        _log = log;
    }

    public async Task PostCommentAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.IdentityKind != GitHubIdentityKind.Pat)
            throw new NotSupportedException(
                $"GitHub comment write-back for identity kind '{connection.IdentityKind}' is not supported yet (GitHub App installation tokens arrive with the full write-back writer)");

        var pat = await _secrets.LoadAsync(
            GitHubConnectionStore.ApiSecretAddress(connection.ProjectId, connection.Id), ct);
        if (pat is null || pat.Length == 0)
            throw new InvalidOperationException(
                $"GitHub connection '{connection.Id}' has no ':api' PAT secret configured");

        var url = $"/repos/{connection.Owner}/{connection.Repo}/issues/{githubIssueNumber}/comments";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new JsonObject { ["body"] = body }),
        };
        request.Headers.Authorization = new("Bearer", System.Text.Encoding.UTF8.GetString(pat));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            _log.LogWarning(
                "GitHub comment post to {Url} failed with {Status}: {Detail}",
                url, (int)response.StatusCode, detail);
            response.EnsureSuccessStatusCode();
        }
    }
}
