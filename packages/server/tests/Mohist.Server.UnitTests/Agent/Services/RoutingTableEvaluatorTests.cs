using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure.Events.Matching;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class RoutingTableEvaluatorTests
{
    [Fact]
    public void Evaluate_OrdersRulesAndStopsAfterFirstNonContinueMatch()
    {
        var rules = new[]
        {
            Rule("second", 2, "event.type == \"match\""),
            Rule("first", 1, "event.type == \"match\""),
            Rule("third", 3, "event.type == \"match\""),
        };

        var outcomes = Evaluate(rules, Input(type: "match"));

        Assert.Equal(["first"], outcomes.Select(x => x.Rule.Id));
        Assert.Equal(RuleExecutable.WouldLaunch, outcomes[0].Executable);
    }

    [Fact]
    public void Evaluate_ContinueRulesFanOut()
    {
        var rules = new[]
        {
            Rule("first", 1, "event.type == \"match\"", continueRule: true),
            Rule("second", 2, "event.type == \"match\"", continueRule: true),
            Rule("third", 3, "event.type == \"match\""),
        };

        var outcomes = Evaluate(rules, Input(type: "match"));

        Assert.Equal(["first", "second", "third"], outcomes.Select(x => x.Rule.Id));
    }

    [Fact]
    public void Evaluate_SkippedMatchesContinueToNextRule()
    {
        var rules = new[]
        {
            Rule("inactive", 1, "event.type == \"match\"", agentId: "inactive"),
            Rule("empty", 2, "event.type == \"match\"", agentId: "empty"),
            Rule("fallback", 3, "event.type == \"match\""),
        };
        var probe = new Probe(activeAgentIds: new HashSet<string>(["agent", "empty", "fallback"]));

        var outcomes = Evaluate(rules, Input(type: "match"), probe);

        Assert.Equal(
            [RuleExecutable.SkippedInactiveAgent, RuleExecutable.SkippedEmptyPrompt, RuleExecutable.WouldLaunch],
            outcomes.Select(x => x.Executable));
        Assert.All(outcomes.Take(2), outcome => Assert.Equal(RuleMatchResult.Matched, outcome.Match));
    }

    [Fact]
    public void Evaluate_RuntimeFailureIsRecordedAsNonMatch()
    {
        var rules = new[]
        {
            Rule("broken", 1, "event.type == \"match\""),
            Rule("fallback", 2, "event.type == \"match\""),
        };

        var outcomes = Evaluate(rules, new ThrowingInput());

        Assert.Equal(RuleExecutable.SkippedRuntimeError, outcomes[0].Executable);
        Assert.Equal(RuleMatchResult.NotMatched, outcomes[0].Match);
        Assert.Equal("fallback", outcomes[1].Rule.Id);
    }

    [Fact]
    public void Evaluate_IsDeterministic()
    {
        var rules = new[] { Rule("b", 2, "event.type == \"match\""), Rule("a", 1, "event.type == \"no\"") };
        var evaluator = new RoutingTableEvaluator();
        var probe = new Probe();

        var first = evaluator.Evaluate(Input(type: "match"), rules, probe);
        var second = evaluator.Evaluate(Input(type: "match"), rules, probe);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Evaluate_CacheCanBeInvalidatedWithoutChangingResults()
    {
        var cache = new RuleExpressionCache();
        var rule = Rule("rule", 1, "event.type == \"match\"");
        var evaluator = new RoutingTableEvaluator();

        var first = evaluator.Evaluate(Input(type: "match"), [rule], new Probe(), cache);
        cache.Invalidate(rule.Id);
        var second = evaluator.Evaluate(Input(type: "match"), [rule], new Probe(), cache);

        Assert.Equal(first, second);
    }

    private static IReadOnlyList<RuleOutcome> Evaluate(
        IEnumerable<RoutingRule> rules,
        EventMatchInput input,
        Probe? probe = null) =>
        new RoutingTableEvaluator().Evaluate(input, rules, probe ?? new Probe());

    private static RoutingRule Rule(string id, int position, string match, string agentId = "agent", bool continueRule = false) => new()
    {
        Id = id,
        Position = position,
        Match = match,
        AgentId = agentId,
        ResponsePrompt = "prompt",
        Continue = continueRule,
    };

    private static DictionaryInput Input(string? type = null, IReadOnlyDictionary<string, string>? extensions = null) =>
        new(type, extensions);

    private sealed class Probe(IReadOnlySet<string>? activeAgentIds = null) : IRuleExecutionProbe
    {
        private readonly IReadOnlySet<string> _activeAgentIds = activeAgentIds ?? new HashSet<string>(["agent"]);

        public bool IsAgentActive(string agentId, out string? agentName)
        {
            var active = _activeAgentIds.Contains(agentId);
            agentName = active ? agentId : null;
            return active;
        }

        public string RenderPrompt(RoutingRule rule, EventMatchInput input) =>
            rule.AgentId == "empty" ? string.Empty : rule.ResponsePrompt;
    }

    private sealed class DictionaryInput(string? type, IReadOnlyDictionary<string, string>? extensions) : EventMatchInput
    {
        public string GetValue(string attribute) => attribute == "type"
            ? type ?? string.Empty
            : extensions?.GetValueOrDefault(attribute) ?? string.Empty;

        public bool Has(string attribute) => attribute == "type" ? type is not null : extensions?.ContainsKey(attribute) == true;
    }

    private sealed class ThrowingInput : EventMatchInput
    {
        public string GetValue(string attribute) => throw new InvalidOperationException("runtime failure");

        public bool Has(string attribute) => throw new InvalidOperationException("runtime failure");
    }
}
