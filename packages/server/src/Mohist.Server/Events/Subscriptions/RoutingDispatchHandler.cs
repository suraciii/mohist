using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Sessions.Services;

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
        var outcomes = evaluator.Evaluate(new CloudEventEventMatchInput(evt), rules, probe);
        var launcher = services.GetRequiredService<IAgentLauncher>();

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

            var labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GenericAgentSessionMetadata.TriggerEventId] = evt.Id,
                [GenericAgentSessionMetadata.TriggerRuleId] = outcome.Rule.Id,
            };
            await launcher.LaunchAsync(agent, outcome.RenderedPromptPreview!, new AgentLaunchContext(projectId), labels, ct);
        }
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
