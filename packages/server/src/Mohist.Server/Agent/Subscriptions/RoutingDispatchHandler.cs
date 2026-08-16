using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Agent.Subscriptions;

[Subscription(
    Type = "*",
    Identity = "Mohist.Server.Events.Subscriptions.RoutingDispatchHandler")]
public sealed class RoutingDispatchHandler : ICloudEventHandler
{
    internal const string WatchRuleIdPrefix = "watch:";

    private static readonly IReadOnlySet<string> WatchEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        EventCatalog.ReverseDns.StageApprovalRequested,
        EventCatalog.ReverseDns.WorkflowRunFailed,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoutingDispatchHandler> _log;

    public RoutingDispatchHandler(IServiceScopeFactory scopeFactory, ILogger<RoutingDispatchHandler> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt is not null;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => DispatchAsync(evt, ct);

    private async Task DispatchAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadProjectId(evt.Extensions, out var projectId))
        {
            _log.LogDebug("Routing dispatch skipped: event {EventType} {EventId} carries no project id", evt.Type, evt.Id);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var rules = await services.GetRequiredService<RoutingRuleStore>().ListAsync(projectId, includeArchived: false, ct);

        var agentQuerier = services.GetRequiredService<AgentQuerier>();
        var evaluator = services.GetRequiredService<RoutingTableEvaluator>();
        var probe = new DispatchRuleExecutionProbe(projectId, agentQuerier);
        // Envelope-only matching + prompt rendering: workspace / Workflow /
        // Issue state does not affect rule selection. The resolver runs
        // AFTER the evaluator picks a hit.
        var outcomes = rules.Count == 0
            ? (IReadOnlyList<RuleOutcome>)Array.Empty<RuleOutcome>()
            : evaluator.Evaluate(new CloudEventEventMatchInput(evt), rules, probe);
        var launcher = services.GetRequiredService<IAgentLauncher>();
        var resolver = services.GetRequiredService<RoutedAgentLaunchContextResolver>();

        var issueNumber = ResolveEventIssueNumber(evt);

        var watchStore = services.GetRequiredService<WatchEntryStore>();
        var mutedAgentIds = await ResolveMutedAgentsAsync(watchStore, projectId, issueNumber, ct);

        var launchedAgentIds = new HashSet<string>(StringComparer.Ordinal);

        // Issue-491 design D3: envelope-only self-response guard. A rule
        // whose configured AgentId equals the envelope's `agentid` is
        // treated as a non-match so an Agent cannot respond to its own
        // `com.mohist.agent.job.failed` event. Empty-AgentId rules and
        // rules pointing at a different Agent are unaffected — the event
        // routes with the same standing as any other.
        var envelopeAgentId = CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.AgentId);

        foreach (var outcome in outcomes)
        {
            if (outcome.Executable is RuleExecutable.SkippedInactiveAgent or RuleExecutable.SkippedEmptyPrompt or RuleExecutable.SkippedRuntimeError)
            {
                _log.LogWarning("Routing rule {RuleId} skipped for event {EventId}: {Reason}", outcome.Rule.Id, evt.Id, outcome.Executable);
                continue;
            }

            if (outcome.Match != RuleMatchResult.Matched || outcome.Executable != RuleExecutable.WouldLaunch)
                continue;

            if (IsSelfResponse(outcome.Rule.AgentId, envelopeAgentId))
            {
                _log.LogWarning(
                    "Routing rule {RuleId} skipped for event {EventId}: envelope agentid '{EnvelopeAgentId}' matches rule agent id (self-response guard)",
                    outcome.Rule.Id, evt.Id, envelopeAgentId ?? "(none)");
                continue;
            }

            var agent = await agentQuerier.GetByIdAsync(projectId, outcome.Rule.AgentId);
            if (agent is null || !string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
                continue;

            if (IsMutedForEvent(mutedAgentIds, issueNumber, agent.Id))
            {
                _log.LogWarning(
                    "Routing rule {RuleId} hit suppressed by mute for event {EventId} issue {issue} agent {AgentId}",
                    outcome.Rule.Id, evt.Id, issueNumber, agent.Id);
                launchedAgentIds.Add(agent.Id);
                continue;
            }

            var execution = await resolver.ResolveAsync(evt, ct);
            if (!execution.IsReady)
            {
                var (_, _) = PreflightLineage(evt, execution);
                await RecordPreflightFailureAsync(
                    services,
                    agent,
                    outcome,
                    evt,
                    execution,
                    ct);
                launchedAgentIds.Add(agent.Id);
                continue;
            }

            await launcher.LaunchRoutedAsync(
                agent,
                outcome.RenderedPromptPreview!,
                execution.Context!,
                evt,
                outcome.Rule.Id,
                ct);
            launchedAgentIds.Add(agent.Id);
        }

        await LaunchWatchingAgentsAsync(
            services,
            evt,
            projectId,
            issueNumber,
            watchStore,
            agentQuerier,
            resolver,
            launcher,
            launchedAgentIds,
            ct);
    }

    /// <summary>
    /// Envelope-only self-response guard. Returns
    /// <c>true</c> only when the rule carries a non-empty
    /// <c>AgentId</c> AND that id equals the envelope's <c>agentid</c>
    /// lineage. Empty <c>AgentId</c> rules and rules pointing at a
    /// different Agent are unaffected.
    /// </summary>
    internal static bool IsSelfResponse(string? ruleAgentId, string? envelopeAgentId)
    {
        if (string.IsNullOrWhiteSpace(ruleAgentId) || string.IsNullOrWhiteSpace(envelopeAgentId))
            return false;
        return string.Equals(ruleAgentId, envelopeAgentId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Watch-launch pass. Fires only on the
    /// fixed event set (<c>stage.approval-requested</c>,
    /// <c>run.failed</c>) and only when the event carries an issue. Each
    /// watching Agent reuses the routed-launch path with a built-in
    /// prompt and a <c>watch:</c>-prefixed TriggerRuleId (string-prefix
    /// convention per design D8). The shared <paramref name="launchedAgentIds"/>
    /// set enforces one launch per (event, agent) so a rule hit and a
    /// watch for the same Agent collapse to a single launch (D7).
    /// </summary>
    private async Task LaunchWatchingAgentsAsync(
        IServiceProvider services,
        CloudEvent evt,
        string projectId,
        int? issueNumber,
        WatchEntryStore watchStore,
        AgentQuerier agentQuerier,
        RoutedAgentLaunchContextResolver resolver,
        IAgentLauncher launcher,
        HashSet<string> launchedAgentIds,
        CancellationToken ct)
    {
        if (issueNumber is not > 0)
            return;
        if (!WatchEventTypes.Contains(evt.Type))
            return;

        var groups = await watchStore.ListAsync(projectId, issueNumber.Value, ct);
        if (groups.Watching.Count == 0)
            return;

        var prompt = BuildWatchPrompt(evt, issueNumber.Value);

        foreach (var entry in groups.Watching)
        {
            if (!launchedAgentIds.Add(entry.AgentId))
            {
                _log.LogDebug(
                    "Watch launch skipped for event {EventId} issue {issue} agent {AgentId}: already launched in this delivery",
                    evt.Id, issueNumber, entry.AgentId);
                continue;
            }

            var agent = await agentQuerier.GetByIdAsync(projectId, entry.AgentId);
            if (agent is null || !string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
                continue;

            var execution = await resolver.ResolveAsync(evt, ct);
            var ruleId = $"{WatchRuleIdPrefix}{agent.Id}";
            if (!execution.IsReady)
            {
                await RecordWatchPreflightFailureAsync(
                    services,
                    agent,
                    evt,
                    execution,
                    ruleId,
                    ct);
                continue;
            }

            await launcher.LaunchRoutedAsync(
                agent,
                prompt,
                execution.Context!,
                evt,
                ruleId,
                ct);
        }
    }

    /// <summary>
    /// Built-in watch response prompt. Conveys the
    /// triggering event as a fact and instructs the Agent to act on its
    /// own identity instructions. No per-watch ResponsePrompt.
    /// </summary>
    internal static string BuildWatchPrompt(CloudEvent evt, int issueNumber) =>
        $"Watch event {evt.Type} for issue #{issueNumber}. " +
        "Act on your identity instructions.";

    /// <summary>
    /// Returns a per-issue map of <c>(issueNumber -> muted agent ids)</c>.
    /// Returns an empty map when the event carries no issue (the dispatch
    /// path stays a no-op for the muted gate).
    /// </summary>
    private static async Task<IReadOnlyDictionary<int, HashSet<string>>> ResolveMutedAgentsAsync(
        WatchEntryStore watchStore,
        string projectId,
        int? issueNumber,
        CancellationToken ct)
    {
        if (issueNumber is not > 0)
            return new Dictionary<int, HashSet<string>>(0);
        var groups = await watchStore.ListAsync(projectId, issueNumber.Value, ct);
        var muted = new HashSet<string>(groups.Muted.Select(entry => entry.AgentId), StringComparer.Ordinal);
        return new Dictionary<int, HashSet<string>>(1) { [issueNumber.Value] = muted };
    }

    private static bool IsMutedForEvent(
        IReadOnlyDictionary<int, HashSet<string>> mutedByIssue,
        int? issueNumber,
        string agentId)
    {
        if (issueNumber is not > 0)
            return false;
        return mutedByIssue.TryGetValue(issueNumber.Value, out var mutedSet)
            && mutedSet.Contains(agentId);
    }

    private static int? ResolveEventIssueNumber(CloudEvent evt) =>
        CloudEventLineage.TryReadPositiveNumber(evt.Extensions, EventCatalog.Lineage.Issue, out var issueNumber)
            ? issueNumber
            : null;

    /// <summary>
    /// Record a watch-driven routed-launch preflight failure as a failed
    /// AgentJob + AgentSession without dispatching a Runner. Mirrors the
    /// routing-rule preflight failure helper.
    /// </summary>
    private async Task RecordWatchPreflightFailureAsync(
        IServiceProvider services,
        AgentInfo agent,
        CloudEvent evt,
        RoutedExecutionContextResolution resolution,
        string ruleId,
        CancellationToken ct)
    {
        var detail = resolution.FailureMessage ?? "watch launch preflight failed";
        var reason = $"{AgentJobFailureReasons.WorkspaceUnavailable}: {detail}";
        var category = AgentJobFailureReasons.WorkspaceUnavailable;

        var triggerIdentity = ResolveWatchPreflightTriggerIdentity(evt, ruleId, services);
        if (triggerIdentity is null)
        {
            _log.LogWarning(
                "Watch launch preflight failure for event {EventId} agent {AgentId} could not mint trigger identity; outcome not recorded",
                evt.Id, agent.Id);
            return;
        }

        var (sessionId, jobKey) = triggerIdentity.Value;
        var jobGrain = services.GetRequiredService<IGrainFactory>().GetGrain<IAgentJobGrain>(jobKey);
        var (issueNumber, epicNumber) = PreflightLineage(evt, resolution);
        var prompt = BuildWatchPrompt(evt, issueNumber ?? 0);
        var definition = AgentLauncher.ResolveExecutionDefinition(agent);
        var preflightPlan = new RoutedAgentLaunchPlan(
            ProjectId: CloudEventLineage.TryReadProjectId(evt.Extensions, out var pid) ? pid : string.Empty,
            EventId: evt.Id,
            RuleId: ruleId,
            SessionId: sessionId,
            JobKey: jobKey,
            IssueNumber: issueNumber,
            EpicNumber: epicNumber,
            WorkspacePath: null,
            Disposition: RoutedLaunchDisposition.PreflightFailed,
            PreflightReason: reason,
            PreflightCategory: category,
            PreparedAt: services.GetRequiredService<TimeProvider>().GetUtcNow(),
            AgentId: agent.Id,
            AgentName: agent.Name,
            AgentInstructions: string.IsNullOrWhiteSpace(definition.Instructions) ? null : definition.Instructions,
            AgentConfigJson: agent.AgentConfig is { ValueKind: not System.Text.Json.JsonValueKind.Undefined } config ? config.GetRawText() : null,
            Model: definition.Model,
            Variant: definition.Variant,
            ReasoningEffort: definition.ReasoningEffort,
            Prompt: prompt,
            Runtime: definition.Runtime,
            Skills: definition.Skills,
            WorkflowRunId: CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.WorkflowRunId));
        await jobGrain.EnsurePreparedAsync(preflightPlan);
        await jobGrain.AdvancePreparedLaunchAsync();
    }

    private static (string SessionId, string JobKey)? ResolveWatchPreflightTriggerIdentity(
        CloudEvent evt,
        string ruleId,
        IServiceProvider services)
    {
        if (!CloudEventLineage.TryReadProjectId(evt.Extensions, out var projectId)
            || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var sessionResolver = services.GetRequiredService<AgentSessionResolver>();
        var sessionId = sessionResolver.StableSessionId(projectId, evt.Id, ruleId);
        var jobKey = sessionResolver.StableJobKey(projectId, evt.Id, ruleId);
        return (sessionId, jobKey);
    }

    /// <summary>
    /// Record a routed-launch preflight failure as a failed AgentJob +
    /// AgentSession without dispatching a Runner. The grain's
    /// preflight-failed terminal-delivery protocol handles the durable close fact.
    /// </summary>
    private async Task RecordPreflightFailureAsync(
        IServiceProvider services,
        AgentInfo agent,
        RuleOutcome outcome,
        CloudEvent evt,
        RoutedExecutionContextResolution resolution,
        CancellationToken ct)
    {
        var detail = resolution.FailureMessage ?? "routed launch preflight failed";
        var reason = $"{AgentJobFailureReasons.WorkspaceUnavailable}: {detail}";
        var category = AgentJobFailureReasons.WorkspaceUnavailable;

        var triggerIdentity = ResolvePreflightTriggerIdentity(evt, outcome, services);
        if (triggerIdentity is null)
        {
            _log.LogWarning(
                "Routing rule {RuleId} preflight failure for event {EventId} could not mint trigger identity; outcome not recorded",
                outcome.Rule.Id, evt.Id);
            return;
        }

        var (sessionId, jobKey) = triggerIdentity.Value;
        var jobGrain = services.GetRequiredService<IGrainFactory>().GetGrain<IAgentJobGrain>(jobKey);
        var (issueNumber, epicNumber) = PreflightLineage(evt, resolution);
        var definition = AgentLauncher.ResolveExecutionDefinition(agent);
        var preflightPlan = new RoutedAgentLaunchPlan(
            ProjectId: CloudEventLineage.TryReadProjectId(evt.Extensions, out var pid) ? pid : string.Empty,
            EventId: evt.Id,
            RuleId: outcome.Rule.Id,
            SessionId: sessionId,
            JobKey: jobKey,
            IssueNumber: issueNumber,
            EpicNumber: epicNumber,
            WorkspacePath: null,
            Disposition: RoutedLaunchDisposition.PreflightFailed,
            PreflightReason: reason,
            PreflightCategory: category,
            PreparedAt: services.GetRequiredService<TimeProvider>().GetUtcNow(),
            AgentId: agent.Id,
            AgentName: agent.Name,
            AgentInstructions: string.IsNullOrWhiteSpace(definition.Instructions) ? null : definition.Instructions,
            AgentConfigJson: agent.AgentConfig is { ValueKind: not System.Text.Json.JsonValueKind.Undefined } config ? config.GetRawText() : null,
            Model: definition.Model,
            Variant: definition.Variant,
            ReasoningEffort: definition.ReasoningEffort,
            Prompt: outcome.RenderedPromptPreview,
            Runtime: definition.Runtime,
            Skills: definition.Skills,
            WorkflowRunId: CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.WorkflowRunId));
        await jobGrain.EnsurePreparedAsync(preflightPlan);
        await jobGrain.AdvancePreparedLaunchAsync();
    }

    internal static (int? IssueNumber, int? EpicNumber) PreflightLineage(
        CloudEvent evt,
        RoutedExecutionContextResolution resolution)
    {
        var issueNumber = CloudEventLineage.TryReadPositiveNumber(
            evt.Extensions, EventCatalog.Lineage.Issue, out var issue)
            ? issue
            : resolution.IssueNumber;
        var epicNumber = CloudEventLineage.TryReadPositiveNumber(
            evt.Extensions, EventCatalog.Lineage.Epic, out var epic)
            ? epic
            : resolution.EpicNumber;
        return (issueNumber, epicNumber);
    }

    private static (string SessionId, string JobKey)? ResolvePreflightTriggerIdentity(
        CloudEvent evt,
        RuleOutcome outcome,
        IServiceProvider services)
    {
        if (!CloudEventLineage.TryReadProjectId(evt.Extensions, out var projectId)
            || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var sessionResolver = services.GetRequiredService<AgentSessionResolver>();
        var sessionId = sessionResolver.StableSessionId(projectId, evt.Id, outcome.Rule.Id);
        var jobKey = sessionResolver.StableJobKey(projectId, evt.Id, outcome.Rule.Id);
        return (sessionId, jobKey);
    }

    private sealed class DispatchRuleExecutionProbe : IRuleExecutionProbe
    {
        private readonly string _projectId;
        private readonly AgentQuerier _agents;

        public DispatchRuleExecutionProbe(string projectId, AgentQuerier agents)
        {
            _projectId = projectId;
            _agents = agents;
        }

        public bool IsAgentActive(string agentId, out string? agentName)
        {
            var agent = _agents.GetByIdAsync(_projectId, agentId).GetAwaiter().GetResult();
            agentName = agent?.Name;
            return agent is not null && string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal);
        }

        public string RenderPrompt(RoutingRule rule, EventMatchInput input) => ResponsePromptRenderer.Render(rule.ResponsePrompt, input);
    }
}
