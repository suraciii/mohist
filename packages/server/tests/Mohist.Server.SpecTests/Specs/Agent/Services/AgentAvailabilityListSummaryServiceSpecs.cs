using Mohist.Server.Agent.Services;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

/// <summary>
/// Spec coverage for the list-scoped
/// <see cref="AgentAvailabilityService.GetListSummaryAsync"/> entrypoint
/// (issue #133 / T-001). The endpoint the Web Agents list renders against
/// distinguishes a Runner-wide Availability signal from a per-Agent
/// readiness verdict: a Ready Agent that is waiting because no Runner is
/// online (or the runner pool is full, or the agent's own concurrency
/// limit has been reached) must read as Availability, not as a
/// configuration gap. Counts surfaced through the summary
/// (<c>activeRuns</c>, <c>queuedCount</c>) come from the concurrency grain
/// and the pending-jobs table respectively, while runner capacity is
/// fetched exactly once across the whole list.
/// </summary>
public sealed class AgentAvailabilityListSummaryServiceSpecs
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentInfo ReadyAgent = new(
        Id: "agent-ready",
        ProjectId: "proj-x",
        Name: "ready",
        Description: "ready agent",
        Instructions: "do the work",
        AgentConfig: null,
        Skills: [],
        MaxConcurrentRuns: 2,
        Status: "active",
        CreatedAt: "2026-07-30T00:00:00Z",
        UpdatedAt: "2026-07-30T00:00:00Z");

    [Fact]
    public void BuildListEntry_ReadyAgentWithNoOnlineRunner_ReportsNoOnlineRunnerAvailability()
    {
        var entry = AgentAvailabilityService.BuildListEntry(
            ReadyAgent,
            capacity: new RunnerCapacityView(0, 0),
            activeRuns: 0,
            queuedCount: 0,
            hasOnlineRunner: false,
            ObservedAt);

        Assert.False(entry.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.NoOnlineRunner, entry.WaitingReason);
        Assert.Equal(0, entry.ActiveRuns);
        Assert.Equal(0, entry.QueuedCount);
        Assert.Equal(2, entry.MaxConcurrentRuns);
        Assert.Equal(new RunnerCapacityView(0, 0), entry.Capacity);
    }

    [Fact]
    public void BuildListEntry_ReadyAgentFreeCapacityAndUnderConcurrency_ReportsCanStartNow()
    {
        var entry = AgentAvailabilityService.BuildListEntry(
            ReadyAgent,
            capacity: new RunnerCapacityView(1, 4),
            activeRuns: 1,
            queuedCount: 0,
            hasOnlineRunner: true,
            ObservedAt);

        Assert.True(entry.CanStartNow);
        Assert.Null(entry.WaitingReason);
        Assert.Equal(1, entry.ActiveRuns);
        Assert.Equal(0, entry.QueuedCount);
        Assert.Equal(new RunnerCapacityView(1, 4), entry.Capacity);
    }

    [Fact]
    public void BuildListEntry_RunnerPoolFull_ReportsCapacityFullAvailability()
    {
        var entry = AgentAvailabilityService.BuildListEntry(
            ReadyAgent,
            capacity: new RunnerCapacityView(4, 4),
            activeRuns: 0,
            queuedCount: 2,
            hasOnlineRunner: true,
            ObservedAt);

        Assert.False(entry.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.CapacityFull, entry.WaitingReason);
        Assert.Equal(2, entry.QueuedCount);
        Assert.Equal(0, entry.ActiveRuns);
    }

    [Fact]
    public void BuildListEntry_AgentConcurrencyLimitReached_ReportsConcurrencyLimitAvailability()
    {
        var entry = AgentAvailabilityService.BuildListEntry(
            ReadyAgent,
            capacity: new RunnerCapacityView(0, 4),
            activeRuns: 2,
            queuedCount: 1,
            hasOnlineRunner: true,
            ObservedAt);

        Assert.False(entry.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.ConcurrencyLimit, entry.WaitingReason);
        Assert.Equal(2, entry.ActiveRuns);
        Assert.Equal(1, entry.QueuedCount);
    }

    [Fact]
    public void BuildListEntry_NoMaxConcurrentRuns_SkipsConcurrencyLimitCheck()
    {
        var unlimited = ReadyAgent with { MaxConcurrentRuns = null };

        var entry = AgentAvailabilityService.BuildListEntry(
            unlimited,
            capacity: new RunnerCapacityView(0, 4),
            activeRuns: 99,
            queuedCount: 5,
            hasOnlineRunner: true,
            ObservedAt);

        Assert.True(entry.CanStartNow);
        Assert.Null(entry.MaxConcurrentRuns);
        Assert.Equal(99, entry.ActiveRuns);
        Assert.Equal(5, entry.QueuedCount);
    }
}
