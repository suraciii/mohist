using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.Tests.Agent;

[Trait("level", "L0")]
public sealed class AgentAvailabilityServiceTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_online_runner_is_the_waiting_conclusion()
    {
        var result = AgentAvailabilityService.Compute(new RunnerCapacityView(0, 0), 0, 2, ObservedAt, false);

        Assert.False(result.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.NoOnlineRunner, result.WaitingReason);
    }

    [Fact]
    public void Online_runners_with_no_free_slot_report_capacity_full()
    {
        var result = AgentAvailabilityService.Compute(new RunnerCapacityView(2, 2), 0, 2, ObservedAt, true);

        Assert.False(result.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.CapacityFull, result.WaitingReason);
    }

    [Fact]
    public void Reaching_the_agent_limit_reports_concurrency_limit()
    {
        var result = AgentAvailabilityService.Compute(new RunnerCapacityView(0, 2), 2, 2, ObservedAt, true);

        Assert.False(result.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.ConcurrencyLimit, result.WaitingReason);
    }

    [Fact]
    public void Free_runner_slot_and_concurrency_report_can_start_now()
    {
        var result = AgentAvailabilityService.Compute(new RunnerCapacityView(1, 2), 1, 2, ObservedAt, true);

        Assert.True(result.CanStartNow);
        Assert.Null(result.WaitingReason);
        Assert.Equal(ObservedAt, result.ObservedAt);
    }

    [Fact]
    public void Incomplete_owner_evidence_reports_dispatch_pending_with_unknown_active_count()
    {
        var result = AgentAvailabilityService.Compute(
            new RunnerCapacityView(1, 2),
            activeRuns: null,
            maxConcurrentRuns: 2,
            ObservedAt,
            hasOnlineRunner: true,
            capacityIncomplete: true);

        Assert.False(result.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.DispatchPending, result.WaitingReason);
        Assert.True(result.CapacityIncomplete);
        // An unknown active count is never coerced to zero.
        Assert.Null(result.ActiveRuns);
    }

    [Fact]
    public void Missing_online_runner_outranks_incomplete_owner_evidence()
    {
        var result = AgentAvailabilityService.Compute(
            new RunnerCapacityView(0, 0),
            activeRuns: null,
            maxConcurrentRuns: null,
            ObservedAt,
            hasOnlineRunner: false,
            capacityIncomplete: true);

        Assert.False(result.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.NoOnlineRunner, result.WaitingReason);
        Assert.True(result.CapacityIncomplete);
    }

    [Fact]
    public void Unknown_active_count_alone_is_not_a_ready_conclusion()
    {
        var result = AgentAvailabilityService.Compute(
            new RunnerCapacityView(1, 2),
            activeRuns: null,
            maxConcurrentRuns: 2,
            ObservedAt,
            hasOnlineRunner: true);

        Assert.False(result.CanStartNow);
        Assert.Equal(AgentAvailabilityWaitReasons.DispatchPending, result.WaitingReason);
    }

    [Fact]
    public void Waiting_work_is_distinguishable_and_has_a_server_reason()
    {
        var pending = new[]
        {
            new AgentJobListItem("job-capacity", "agent-1", "pending", "2026-07-29T12:00:00Z", null),
            new AgentJobListItem("job-concurrency", "agent-1", "pending", "2026-07-29T12:01:00Z", null),
        };

        var waiting = AgentAvailabilityService.BuildWaitingWork(
            pending,
            AgentAvailabilityWaitReasons.CapacityFull);

        Assert.All(waiting, item => Assert.Equal("waiting", item.Status));
        Assert.Equal(AgentAvailabilityWaitReasons.CapacityFull, waiting[0].WaitingReason);
        Assert.Equal(AgentAvailabilityWaitReasons.CapacityFull, waiting[1].WaitingReason);
    }

    [Fact]
    public void Waiting_work_reports_dispatch_pending_when_capacity_recovers()
    {
        var pending = new[]
        {
            new AgentJobListItem(
                "job-capacity",
                "agent-1",
                "pending",
                "2026-07-29T12:00:00Z",
                null),
        };

        var waiting = AgentAvailabilityService.BuildWaitingWork(pending, availabilityReason: null);

        Assert.Equal(AgentAvailabilityWaitReasons.DispatchPending, Assert.Single(waiting).WaitingReason);
    }
}
