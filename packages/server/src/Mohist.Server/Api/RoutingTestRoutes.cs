using Mohist.Server.Agent.Services;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure.Events.Matching;

namespace Mohist.Server.Api;

public static class RoutingTestRoutes
{
    public const int DefaultLast = 20;

    public static WebApplication MapRoutingTestRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/routing")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/test", async (
            HttpContext context,
            int? last,
            RoutingRuleStore rules,
            ProjectRecentEventReader events,
            RoutingTableEvaluator evaluator,
            AgentQuerier agents,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var activeRules = await rules.ListAsync(project.Id, includeArchived: false, ct);
            if (activeRules.Count == 0)
                return ApiResults.Ok(new RoutingTestResponse(
                    project.Id,
                    last is null ? DefaultLast : NormalizeLimit(last.Value),
                    "No active routing rules are configured for this project.",
                    []));

            var replayed = await events.ListAsync(project.Id, last ?? DefaultLast, ct);
            if (replayed.Count == 0)
                return ApiResults.Ok(new RoutingTestResponse(
                    project.Id,
                    last is null ? DefaultLast : NormalizeLimit(last.Value),
                    "No replayable events are available for this project.",
                    []));

            var probe = new DryRunRuleExecutionProbe(project.Id, agents);
            var traces = replayed.Select(evt => new RoutingTestEventTrace(
                evt.EventId,
                evt.Type,
                evt.Source,
                evt.Time,
                evaluator.Evaluate(evt.Input, activeRules, probe).Select(ToTrace).ToArray())).ToArray();

            return ApiResults.Ok(new RoutingTestResponse(project.Id, NormalizeLimit(last ?? DefaultLast), null, traces));
        });

        return app;
    }

    private static int NormalizeLimit(int value) => value <= 0 ? DefaultLast : value;

    private static RoutingTestRuleTrace ToTrace(RuleOutcome outcome) => new(
        outcome.Rule.Id,
        outcome.Rule.Name,
        outcome.Rule.Position,
        outcome.Match == RuleMatchResult.Matched,
        outcome.Rule.Continue,
        outcome.Match == RuleMatchResult.Matched && outcome.Executable == RuleExecutable.WouldLaunch
            ? outcome.Rule.Continue ? "continue" : "stop"
            : null,
        outcome.Executable == RuleExecutable.WouldLaunch ? outcome.ResolvedAgentName : null,
        outcome.Executable.ToString());

    private sealed class DryRunRuleExecutionProbe : IRuleExecutionProbe
    {
        private readonly string _projectId;
        private readonly AgentQuerier _agents;

        public DryRunRuleExecutionProbe(string projectId, AgentQuerier agents)
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

        public string RenderPrompt(RoutingRule rule, EventMatchInput input) =>
            ResponsePromptRenderer.Render(rule.ResponsePrompt, input);
    }
}

public sealed record RoutingTestResponse(
    string ProjectId,
    int Last,
    string? Message,
    IReadOnlyList<RoutingTestEventTrace> Events);

public sealed record RoutingTestEventTrace(
    string EventId,
    string Type,
    string Source,
    DateTimeOffset Time,
    IReadOnlyList<RoutingTestRuleTrace> Rules);

public sealed record RoutingTestRuleTrace(
    string RuleId,
    string RuleName,
    int Position,
    bool Matched,
    bool Continue,
    string? Decision,
    string? WouldTriggerAgent,
    string Outcome);
