using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Close translator: a GitHub <c>closed</c> event withdraws the demand —
/// the linked Mohist issue is cancelled. Terminal issues (Done/Cancelled)
/// are a no-op, which keeps the close self-loop safe: when Mohist completes
/// an issue, the future write-back closes the GitHub issue, and the echoed
/// <c>closed</c> event hits the terminal check without any identity-based
/// dedup. Events without a link (e.g. <c>closed</c> arriving before
/// <c>labeled</c>) are accepted as a v1 no-op, per the design's ordering
/// decision.
/// <para>
/// An issue whose workflow is still running cannot be cancelled (the
/// aggregate refuses); the handler treats that as a no-op too — the demand
/// withdrawal lands once the run settles, and the GitHub issue stays
/// closed either way.
/// </para>
/// </summary>
[Subscription(
    Type = EventCatalog.ReverseDns.GitHubIssuesClosed,
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueCloseHandler")]
public sealed class GitHubIssueCloseHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<GitHubIssueCloseHandler> _log;

    public GitHubIssueCloseHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<GitHubIssueCloseHandler> log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && string.Equals(evt.Type, EventCatalog.ReverseDns.GitHubIssuesClosed, StringComparison.Ordinal)
        && IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out _, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => CloseAsync(evt, ct);

    private async Task CloseAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out var projectId, out var connectionId))
            return;
        var payload = GitHubIssueEventPayload.Parse(evt.Data);
        if (payload is null)
        {
            _log.LogDebug("GitHub close skipped: event {EventId} carries no readable issue payload", evt.Id);
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connection = await sp.GetRequiredService<GitHubConnectionStore>().GetByIdAsync(connectionId, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;

        var link = await sp.GetRequiredService<GitHubIssueLinkStore>()
            .GetAsync(projectId, connection.RepositoryName, payload.IssueNumber, ct);
        if (link is null)
            return;

        var issueStore = sp.GetRequiredService<IIssueStore>();
        var issue = await issueStore.LoadAsync(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
        if (issue is null)
            return;
        if (issue.Status is IssueStatus.Done or IssueStatus.Cancelled)
            return;

        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
        try
        {
            await grain.CancelAsync();
        }
        catch (InvalidOperationException ex)
        {
            _log.LogDebug(
                "GitHub close no-op: issue #{IssueNumber} cannot be cancelled ({Message})",
                link.IssueNumber, ex.Message);
        }
    }
}
