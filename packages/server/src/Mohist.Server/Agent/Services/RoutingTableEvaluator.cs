using System.Text.RegularExpressions;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public enum RuleMatchResult
{
    NotMatched,
    Matched,
}

public enum RuleExecutable
{
    WouldLaunch,
    SkippedInactiveAgent,
    SkippedEmptyPrompt,
    SkippedRuntimeError,
}

public sealed record RuleOutcome(
    RoutingRule Rule,
    RuleMatchResult Match,
    RuleExecutable Executable,
    string? ResolvedAgentName = null,
    string? RenderedPromptPreview = null);

public interface IRuleExecutionProbe
{
    bool IsAgentActive(string agentId, out string? agentName);

    string RenderPrompt(RoutingRule rule, EventMatchInput input);
}

public interface IRuleExpressionCache
{
    bool TryGet(string ruleId, string match, out EventMatchExpression expression);

    void Set(string ruleId, string match, EventMatchExpression expression);

    void Invalidate(string ruleId);
}

public sealed class RuleExpressionCache : IRuleExpressionCache
{
    private readonly Dictionary<string, EventMatchExpression> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string ruleId, string match, out EventMatchExpression expression) =>
        _entries.TryGetValue(Key(ruleId, match), out expression!);

    public void Set(string ruleId, string match, EventMatchExpression expression) =>
        _entries[Key(ruleId, match)] = expression;

    public void Invalidate(string ruleId)
    {
        foreach (var key in _entries.Keys.Where(key => key.StartsWith(ruleId + "\n", StringComparison.Ordinal)).ToArray())
            _entries.Remove(key);
    }

    private static string Key(string ruleId, string match) => $"{ruleId}\n{match}";
}

public sealed class RoutingTableEvaluator : IScopedService
{
    public IReadOnlyList<RuleOutcome> Evaluate(
        EventMatchInput input,
        IEnumerable<RoutingRule> rules,
        IRuleExecutionProbe probe,
        IRuleExpressionCache? expressionCache = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(probe);

        var outcomes = new List<RuleOutcome>();
        foreach (var rule in rules.OrderBy(rule => rule.Position).ThenBy(rule => rule.Id, StringComparer.Ordinal))
        {
            var failureSink = new RecordingFailureSink();
            var expression = GetExpression(rule, failureSink, expressionCache);
            var matched = expression is not null && expression.Matches(input);
            if (!matched)
            {
                outcomes.Add(new RuleOutcome(
                    rule,
                    RuleMatchResult.NotMatched,
                    failureSink.HasFailure ? RuleExecutable.SkippedRuntimeError : RuleExecutable.WouldLaunch));
                continue;
            }

            if (!probe.IsAgentActive(rule.AgentId, out var agentName))
            {
                outcomes.Add(new RuleOutcome(rule, RuleMatchResult.Matched, RuleExecutable.SkippedInactiveAgent, agentName));
                continue;
            }

            var renderedPrompt = probe.RenderPrompt(rule, input);
            if (string.IsNullOrWhiteSpace(renderedPrompt))
            {
                outcomes.Add(new RuleOutcome(rule, RuleMatchResult.Matched, RuleExecutable.SkippedEmptyPrompt, agentName, renderedPrompt));
                continue;
            }

            outcomes.Add(new RuleOutcome(rule, RuleMatchResult.Matched, RuleExecutable.WouldLaunch, agentName, renderedPrompt));
            if (!rule.Continue)
                break;
        }

        return outcomes;
    }

    private static EventMatchExpression? GetExpression(
        RoutingRule rule,
        IEventMatchFailureSink failureSink,
        IRuleExpressionCache? cache)
    {
        if (cache is not null && cache.TryGet(rule.Id, rule.Match, out var cached))
        {
            var cachedResult = EventMatchExpression.Compile(rule.Match, failureSink: failureSink);
            return cachedResult.Expression is null ? cached : cachedResult.Expression;
        }

        var result = EventMatchExpression.Compile(rule.Match, failureSink: failureSink);
        if (result.Expression is not null)
            cache?.Set(rule.Id, rule.Match, result.Expression);
        return result.Expression;
    }

    private sealed class RecordingFailureSink : IEventMatchFailureSink
    {
        public bool HasFailure { get; private set; }

        public void Record(string source, Exception exception) => HasFailure = true;
    }
}
