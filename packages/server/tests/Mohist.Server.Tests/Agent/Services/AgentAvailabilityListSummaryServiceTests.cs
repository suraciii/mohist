using Mohist.Server.Agent.Services;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.Tests.Agent.Services;

/// <summary>
/// Spec coverage for the list-scoped
/// <see cref="AgentAvailabilityService.GetListSummaryAsync"/> entrypoint
/// (issue #133 / T-001). The endpoint the Web Agents list renders against
/// distinguishes a Runner-wide Availability signal from a per-Agent
/// readiness verdict: a Ready Agent that is waiting because no Runner is
/// online (or the runner pool is full, or the agent's own concurrency
/// limit has been reached) must read as Availability, not as a
/// configuration gap. Counts surfaced through the summary
/// (<c>activeRuns</c>, <c>queuedCount</c>) come from one batched derived
/// owner read, and the live concurrency limit comes from that same
/// snapshot rather than the Agent definition the list was hydrated
/// from; runner capacity is fetched exactly once across the whole list.
/// </summary>
[Trait("level", "L0")]
public sealed class AgentAvailabilityListSummaryServiceTests
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
            ObservedAt,
            maxConcurrentRuns: 2);

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
            ObservedAt,
            maxConcurrentRuns: 2);

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
            ObservedAt,
            maxConcurrentRuns: 2);

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
            ObservedAt,
            maxConcurrentRuns: 2);

        Assert.False(entry.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.ConcurrencyLimit, entry.WaitingReason);
        Assert.Equal(2, entry.ActiveRuns);
        Assert.Equal(1, entry.QueuedCount);
    }

    [Fact]
    public void BuildListEntry_NoMaxConcurrentRuns_SkipsConcurrencyLimitCheck()
    {
        var entry = AgentAvailabilityService.BuildListEntry(
            ReadyAgent,
            capacity: new RunnerCapacityView(0, 4),
            activeRuns: 99,
            queuedCount: 5,
            hasOnlineRunner: true,
            ObservedAt,
            maxConcurrentRuns: null);

        Assert.True(entry.CanStartNow);
        Assert.Null(entry.MaxConcurrentRuns);
        Assert.Equal(99, entry.ActiveRuns);
        Assert.Equal(5, entry.QueuedCount);
    }

    [Fact]
    public void BuildListEntry_LiveLimitFromSnapshotOverridesStaleDefinitionLimit()
    {
        // The definition snapshot the list was hydrated from says 4; the
        // derived read says the Agent's current limit is 1 and one run holds
        // it. The live limit decides, not the stale definition.
        var entry = AgentAvailabilityService.BuildListEntry(
            ReadyAgent,
            capacity: new RunnerCapacityView(0, 4),
            activeRuns: 1,
            queuedCount: 1,
            hasOnlineRunner: true,
            ObservedAt,
            maxConcurrentRuns: 1);

        Assert.False(entry.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.ConcurrencyLimit, entry.WaitingReason);
        Assert.Equal(1, entry.MaxConcurrentRuns);
    }

    [Fact]
    public void BuildListEntry_IncompleteOwnerEvidence_NullsCountsAndReportsDispatchPending()
    {
        var entry = AgentAvailabilityService.BuildListEntry(
            ReadyAgent,
            capacity: new RunnerCapacityView(1, 4),
            activeRuns: null,
            queuedCount: null,
            hasOnlineRunner: true,
            ObservedAt,
            maxConcurrentRuns: null,
            capacityIncomplete: true);

        Assert.False(entry.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.DispatchPending, entry.WaitingReason);
        Assert.True(entry.CapacityIncomplete);
        Assert.Null(entry.ActiveRuns);
        Assert.Null(entry.QueuedCount);
    }
}
