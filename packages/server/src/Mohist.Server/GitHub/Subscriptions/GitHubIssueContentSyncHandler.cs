using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.GitHub.Subscriptions;

[Subscription(
    Type = "com.mohist.github.issues.edited",
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueContentSyncHandler")]
public sealed class GitHubIssueContentSyncHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<GitHubIssueContentSyncHandler> _log;

    public GitHubIssueContentSyncHandler(IServiceScopeFactory scopes, IGrainFactory grains, ILogger<GitHubIssueContentSyncHandler> log)
    { _scopes = scopes; _grains = grains; _log = log; }

    public bool Filter(CloudEvent evt) => evt is not null
        && evt.Type == EventCatalog.ReverseDns.GitHubIssuesEdited
        && IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out _, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => HandleCoreAsync(evt, ct);

    private async Task HandleCoreAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out var projectId, out var connectionId)) return;
        var payload = GitHubIssueEventPayload.Parse(evt.Data);
        if (payload is null) return;
        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connection = await sp.GetRequiredService<GitHubConnectionStore>().GetByIdAsync(connectionId, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active) return;
        var link = await sp.GetRequiredService<GitHubIssueLinkStore>().GetAsync(projectId, connection.RepositoryName, payload.IssueNumber, ct);
        if (link is null) return;
        var body = link.MirrorMarker is null ? payload.Body : GitHubMirrorMarker.Strip(payload.Body, link.MirrorMarker);
        var issue = await sp.GetRequiredService<IIssueStore>()
            .LoadAsync(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
        if (issue is null) return;
        if (string.Equals(payload.Title, issue.Title, StringComparison.Ordinal)
            && string.Equals(body, issue.Body, StringComparison.Ordinal)) return;
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
        try
        { await grain.UpdateFromGitHubAsync(payload.Title, body, $"github:{payload.EditorLogin ?? "unknown"}"); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { _log.LogWarning(ex, "GitHub content sync failed for Mohist issue #{IssueNumber}", link.IssueNumber); }
    }
}
