using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Bus subscription that drives Agent subscription dispatch. For every
/// CloudEvent that the bus passes through, this handler matches each
/// project-scoped subscription against the envelope, arbitrates a single
/// (Agent, subscription) pair by priority, renders the subscription's
/// <c>ResponsePrompt</c>, and invokes the shared
/// <see cref="IAgentLauncher"/> with the trigger correlation labels that
/// spec <c>agent-subscription-visibility</c> requires.
/// </summary>
/// <remarks>
/// <para>
/// The handler subscribes to <c>*</c> because the set of event types
/// users can subscribe to is open-ended and configured at runtime; per-type
/// subscription attributes are reserved for handlers whose event type is
/// hard-coded (e.g. <see cref="InboxProjectionHandler"/>). The envelope
/// is consumed only via <see cref="CloudEvent"/>'s
/// <see cref="CloudEvent.Type"/>, <see cref="CloudEvent.Source"/>,
/// <see cref="CloudEvent.Subject"/>, <see cref="CloudEvent.Data"/> and
/// <see cref="CloudEvent.Extensions"/> — no Workflow / Issue domain
/// reverse-query (spec
/// <c>agent-subscription-dispatch#Subscription dispatch consumes only the
/// CloudEvent envelope</c>).
/// </para>
/// <para>
/// <b>Resolved dependencies via scope.</b> The handler is registered as a
/// singleton by <c>AddCloudEventHandlersFromAssembly</c> because the bus
/// enumerates handler singletons at construction; injecting scoped
/// services through the constructor would lock in a single
/// <see cref="IServiceScopeFactory"/>-resolved instance. Instead the
/// handler opens an <see cref="AsyncServiceScope"/> per dispatch
/// (same pattern as <see cref="InboxProjectionHandler"/>) so the
/// scoped <see cref="AgentSubscriptionStore"/>, <see cref="AgentQuerier"/>
/// and <see cref="IAgentLauncher"/> each see fresh per-event state.
/// </para>
/// <para>
/// <b>Launch boundary.</b> <see cref="IAgentLauncher.LaunchAsync"/>
/// awaits the AgentJobGrain's mint + enqueue but does not block on a
/// runner response, mirroring the manual HTTP launch path's behavior
/// (issue-391 T-001). The dispatch call therefore only blocks for grain
/// mint/enqueue — the runner itself runs out-of-band.
/// </para>
/// <para>
/// <b>Project resolution.</b> Issue events already stamp
/// <c>extensions["projectid"]</c> (see <c>IssueStore.SaveAsync</c>).
/// Workflow events now stamp <c>extensions["projectid"]</c> at production time
/// in <c>WorkflowRunStore.ToCloudEvent</c> using the run's metadata annotations.
/// Events without a project id on the envelope are skipped gracefully.
/// </para>
/// </remarks>
[Subscription(Type = "*")]
public sealed class AgentSubscriptionDispatchHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentSubscriptionDispatchHandler> _log;

    public AgentSubscriptionDispatchHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<AgentSubscriptionDispatchHandler> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt is not null;

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        await DispatchAsync(evt, ct).ConfigureAwait(false);
    }

    private async Task DispatchAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!TryResolveProjectId(evt, out var projectId))
        {
            _log.LogDebug(
                "Subscription dispatch skipped: event {EventType} {EventId} carries no project id on the envelope",
                evt.Type, evt.Id);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AgentSubscriptionStore>();
        var agentQuerier = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();

        var candidates = await store.ListByProjectAsync(projectId, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
            return;

        var active = new List<AgentSubscription>(candidates.Count);
        foreach (var subscription in candidates)
        {
            if (!string.Equals(subscription.Status, SubscriptionStatus.Active, StringComparison.Ordinal))
                continue;
            if (!subscription.Filter.Matches(evt))
                continue;

            var agent = await agentQuerier.GetByIdAsync(projectId, subscription.AgentId).ConfigureAwait(false);
            if (agent is null)
                continue;
            if (!string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
                continue;

            active.Add(subscription);
        }

        if (active.Count == 0)
            return;

        var winner = Arbitrate(active);
        if (winner is null)
            return;

        var winningAgent = await agentQuerier.GetByIdAsync(projectId, winner.AgentId).ConfigureAwait(false);
        if (winningAgent is null)
            return;

        var renderedPrompt = ResponsePromptRenderer.Render(winner.ResponsePrompt, evt);
        if (string.IsNullOrWhiteSpace(renderedPrompt))
        {
            _log.LogDebug(
                "Subscription dispatch skipped: subscription {SubscriptionId} produced an empty response prompt after rendering",
                winner.Id);
            return;
        }

        var triggerLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = evt.Id,
            [GenericAgentSessionMetadata.TriggerSubscriptionId] = winner.Id,
        };

        await launcher.LaunchAsync(
            winningAgent,
            renderedPrompt,
            new AgentLaunchContext(ProjectId: projectId),
            triggerLabels,
            ct).ConfigureAwait(false);

        _log.LogDebug(
            "Subscription dispatch: event {EventType} {EventId} -> agent {AgentId} via subscription {SubscriptionId}",
            evt.Type, evt.Id, winningAgent.Id, winner.Id);
    }

    /// <summary>
    /// Returns the project id stamped on the CloudEvent envelope. Issue
    /// events stamp <c>extensions["projectid"]</c> at production time in
    /// <c>IssueStore.SaveAsync</c>; workflow events stamp it
    /// in <c>WorkflowRunStore.ToCloudEvent</c> from the run's metadata
    /// annotations. Events whose envelope cannot be resolved are skipped.
    /// </summary>
    private static bool TryResolveProjectId(CloudEvent evt, out string projectId)
    {
        return CloudEventLineage.TryReadProjectId(evt.Extensions, out projectId);
    }

    /// <summary>
    /// Picks exactly one subscription from the matched candidate set per
    /// the event-level arbitration rules defined in
    /// <c>design/agent-subscriptions.md</c> and spec
    /// <c>agent-subscription-dispatch#Event-level arbitration</c>:
    /// <list type="number">
    ///   <item>Group matched subscriptions by owning Agent id.</item>
    ///   <item>Score each Agent group by the highest subscription
    ///         <see cref="AgentSubscription.Priority"/> in that group
    ///         (null priority defaults to <c>0</c>).</item>
    ///   <item>Select the group with the highest score; ties broken
    ///         deterministically by the lexicographically smallest
    ///         winning subscription id across tied groups.</item>
    ///   <item>Within the winning group, select the single subscription
    ///         with the highest priority, ties broken deterministically
    ///         by the lexicographically smallest
    ///         <see cref="AgentSubscription.Id"/>.</item>
    /// </list>
    /// </summary>
    internal static AgentSubscription? Arbitrate(IReadOnlyList<AgentSubscription> candidates)
    {
        if (candidates is null || candidates.Count == 0)
            return null;

        AgentSubscription? groupWinner = null;
        var groupWinnerScore = int.MinValue;

        foreach (var group in candidates.GroupBy(s => s.AgentId, StringComparer.Ordinal))
        {
            var groupTop = group
                .OrderByDescending(s => s.Priority ?? 0)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .First();

            var score = groupTop.Priority ?? 0;
            if (groupWinner is null
                || score > groupWinnerScore
                || (score == groupWinnerScore
                    && StringComparer.Ordinal.Compare(groupTop.Id, groupWinner.Id) < 0))
            {
                groupWinnerScore = score;
                groupWinner = groupTop;
            }
        }

        return groupWinner;
    }
}
