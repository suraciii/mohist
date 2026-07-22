using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Events.Subscriptions;

[Subscription(Type = "*")]
public sealed class RoutingDispatchHandler : ICloudEventHandler
{
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
        if (rules.Count == 0)
            return;

        var agentQuerier = services.GetRequiredService<AgentQuerier>();
        var evaluator = services.GetRequiredService<RoutingTableEvaluator>();
        var probe = new DispatchRuleExecutionProbe(projectId, agentQuerier);
        // Envelope-only matching + prompt rendering: workspace / Workflow /
        // Issue state does not affect rule selection (issue-449 design
        // decision 4). The resolver runs AFTER the evaluator picks a hit.
        var outcomes = evaluator.Evaluate(new CloudEventEventMatchInput(evt), rules, probe);
        var launcher = services.GetRequiredService<IAgentLauncher>();
        var resolver = services.GetRequiredService<RoutedAgentLaunchContextResolver>();

        foreach (var outcome in outcomes)
        {
            if (outcome.Executable is RuleExecutable.SkippedInactiveAgent or RuleExecutable.SkippedEmptyPrompt or RuleExecutable.SkippedRuntimeError)
            {
                _log.LogWarning("Routing rule {RuleId} skipped for event {EventId}: {Reason}", outcome.Rule.Id, evt.Id, outcome.Executable);
                continue;
            }

            if (outcome.Match != RuleMatchResult.Matched || outcome.Executable != RuleExecutable.WouldLaunch)
                continue;

            var agent = await agentQuerier.GetByIdAsync(projectId, outcome.Rule.AgentId);
            if (agent is null || !string.Equals(agent.Status, AgentStatus.Active, StringComparison.Ordinal))
                continue;

            var execution = await resolver.ResolveAsync(evt, ct);
            if (!execution.IsReady)
            {
                var (issueNumber, _) = PreflightLineage(evt, execution);
                var issueRuntimeOverride = await ResolveIssueRuntimeOverrideAsync(
                    services,
                    projectId,
                    issueNumber,
                    ct);
                await RecordPreflightFailureAsync(
                    services,
                    agent,
                    outcome,
                    evt,
                    execution,
                    issueRuntimeOverride,
                    ct);
                continue;
            }

            var issueNumberForLaunch = execution.Context!.IssueNumber
                ?? (CloudEventLineage.TryReadPositiveNumber(
                    evt.Extensions,
                    EventCatalog.Lineage.Issue,
                    out var envelopeIssueNumber)
                    ? envelopeIssueNumber
                    : null);
            var runtimeOverride = await ResolveIssueRuntimeOverrideAsync(
                services,
                projectId,
                issueNumberForLaunch,
                ct);
            await launcher.LaunchRoutedAsync(
                agent,
                outcome.RenderedPromptPreview!,
                execution.Context!,
                evt,
                outcome.Rule.Id,
                runtimeOverride,
                ct);
        }
    }

    /// <summary>
    /// Record a routed-launch preflight failure as a failed AgentJob +
    /// AgentSession without dispatching a Runner. The grain's
    /// preflight-failed terminal-delivery protocol (issue-449 design
    /// decision 2) handles the durable close fact.
    /// </summary>
    private async Task RecordPreflightFailureAsync(
        IServiceProvider services,
        AgentInfo agent,
        RuleOutcome outcome,
        CloudEvent evt,
        RoutedExecutionContextResolution resolution,
        string? runtimeOverride,
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
            AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
            AgentConfigJson: agent.AgentConfig is { ValueKind: not System.Text.Json.JsonValueKind.Undefined } config ? config.GetRawText() : null,
            Model: AgentLauncher.ResolveModelAndVariant(agent.AgentConfig).Model,
            Variant: AgentLauncher.ResolveModelAndVariant(agent.AgentConfig).Variant,
            Prompt: outcome.RenderedPromptPreview,
            Runtime: AgentLauncher.ResolveRuntime(agent.AgentConfig, runtimeOverride));
        await jobGrain.EnsurePreparedAsync(preflightPlan);
        await jobGrain.AdvancePreparedLaunchAsync();
    }

    private static async Task<string?> ResolveIssueRuntimeOverrideAsync(
        IServiceProvider services,
        string projectId,
        int? issueNumber,
        CancellationToken ct)
    {
        if (issueNumber is not > 0)
            return null;

        return await services.GetRequiredService<IssueWorkflowProfileManager>()
            .GetAgentRuntimeOverrideAsync(projectId, issueNumber.Value);
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
