using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Maintains the passive Mohist-to-GitHub mirror. Reconciliation and error
/// bookkeeping are shared with the explicit sync and connection-enable paths.
/// </summary>
[Subscription(
    Type = "com.mohist.issue.created|com.mohist.issue.content-changed|com.mohist.issue.draft-changed",
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueMirrorHandler")]
public sealed class GitHubIssueMirrorHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;

    public GitHubIssueMirrorHandler(IServiceScopeFactory scopes) => _scopes = scopes;

    public bool Filter(CloudEvent evt) => evt is not null
        && evt.Type is EventCatalog.ReverseDns.IssueCreated
            or EventCatalog.ReverseDns.IssueContentChanged
            or EventCatalog.ReverseDns.IssueDraftChanged;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => HandleCoreAsync(evt, ct);

    private async Task HandleCoreAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadIssueContext(evt, out var context)) return;
        await using var scope = _scopes.CreateAsyncScope();
        var issue = await scope.ServiceProvider.GetRequiredService<IIssueStore>()
            .LoadAsync(GrainKey.Issue(new IssueKey(context.ProjectId, context.IssueNumber)));
        if (issue is null || issue.IsDraft) return;

        // Feed-imported links already point at the originating GitHub issue and
        // have no Mohist-owned marker. They are not native mirrors; leave their
        // existing feed/write-back behavior unchanged.
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        var existingLink = await links.GetByIssueAsync(context.ProjectId, context.IssueNumber, ct);
        if (existingLink is { IsPending: false, MirrorMarker: null }) return;

        var pushContent = true;
        if (evt.Type == EventCatalog.ReverseDns.IssueContentChanged
            && evt.Data?.Deserialize<IssueContentChanged>(CloudEvent.JsonOptions)?.Source?.StartsWith("github:", StringComparison.Ordinal) == true)
        {
            pushContent = false;
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<GitHubIssueSynchronizationService>()
                .SyncAsync(context.ProjectId, context.IssueNumber, pushContent, evt.Type, ct);
        }
        catch (GitHubSynchronizationException)
        {
            // The service records the failure on the link. Mirror failures are
            // best effort and must not retry the whole Issue event forever.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // An unavailable connection or invalid Issue is a no-op for the
            // passive event; explicit sync exposes those errors to operators.
        }
    }
}
