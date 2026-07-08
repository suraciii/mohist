using Mohist.Server.Agent.Domain;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public class AgentSubscriptionDispatchArbitrationTests
{
    [Fact]
    public void Arbitrate_NoCandidates_ReturnsNull()
    {
        Assert.Null(AgentSubscriptionDispatchHandler.Arbitrate(Array.Empty<AgentSubscription>()));
        Assert.Null(AgentSubscriptionDispatchHandler.Arbitrate(null!));
    }

    [Fact]
    public void Arbitrate_HighPriorityGroupWins()
    {
        var s1 = NewSubscription("subs_a", "agent_a", priority: 5);
        var s2 = NewSubscription("subs_b", "agent_b", priority: 100);

        var winner = AgentSubscriptionDispatchHandler.Arbitrate(new[] { s1, s2 });

        Assert.NotNull(winner);
        Assert.Equal("subs_b", winner!.Id);
    }

    [Fact]
    public void Arbitrate_TiedGroups_DeterministicBySubscriptionId()
    {
        var s1 = NewSubscription("subs_zzz", "agent_a", priority: 10);
        var s2 = NewSubscription("subs_aaa", "agent_b", priority: 10);

        var winner = AgentSubscriptionDispatchHandler.Arbitrate(new[] { s1, s2 });

        Assert.NotNull(winner);
        Assert.Equal("subs_aaa", winner!.Id);
    }

    [Fact]
    public void Arbitrate_TiedWithinAgent_DeterministicBySubscriptionId()
    {
        var s1 = NewSubscription("subs_zzz", "agent_a", priority: 10);
        var s2 = NewSubscription("subs_aaa", "agent_a", priority: 10);

        var winner = AgentSubscriptionDispatchHandler.Arbitrate(new[] { s1, s2 });

        Assert.NotNull(winner);
        Assert.Equal("subs_aaa", winner!.Id);
    }

    private static AgentSubscription NewSubscription(
        string id, string agentId, int? priority) =>
        new()
        {
            Id = id,
            ProjectId = "proj_a",
            AgentId = agentId,
            Name = id,
            Filter = new SubscriptionFilter { Type = "com.mohist.workflow.stage.*" },
            ResponsePrompt = id,
            Priority = priority,
            Status = SubscriptionStatus.Active,
        };
}
