using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

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
    public void Waiting_work_is_distinguishable_and_has_a_server_reason()
    {
        var pending = new[]
        {
            new AgentJobListItem("job-capacity", "agent-1", "pending", "2026-07-29T12:00:00Z", null),
            new AgentJobListItem("job-concurrency", "agent-1", "pending", "2026-07-29T12:01:00Z", null),
        };

        var waiting = AgentAvailabilityService.BuildWaitingWork(
            pending,
            new HashSet<string>(["job-concurrency"], StringComparer.Ordinal),
            AgentAvailabilityWaitReasons.CapacityFull);

        Assert.All(waiting, item => Assert.Equal("waiting", item.Status));
        Assert.Equal(AgentAvailabilityWaitReasons.CapacityFull, waiting[0].WaitingReason);
        Assert.Equal(AgentAvailabilityWaitReasons.ConcurrencyLimit, waiting[1].WaitingReason);
    }
}
