using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentConcurrencyReconciliationSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public AgentConcurrencyReconciliationSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Reconciliation_retains_a_grant_until_the_job_can_persist_its_owner_state()
    {
        var projectId = $"permit-race-project-{Guid.NewGuid():N}";
        var agentId = $"permit-race-agent-{Guid.NewGuid():N}";
        var jobId = $"permit-race-job-{Guid.NewGuid():N}";
        var token = $"{jobId}:execution";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, token, jobId, AgentConcurrencyPermitOwnerKind.Job));

        await RemindAsync(gate, _fixture.TimeProvider.GetUtcNow());

        Assert.Equal(1, await gate.GetActiveCountAsync());

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await RemindAsync(gate, _fixture.TimeProvider.GetUtcNow());

        Assert.Equal(0, await gate.GetActiveCountAsync());

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, token, jobId, AgentConcurrencyPermitOwnerKind.Job));
        Assert.Equal(1, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task Releasing_after_limit_becomes_unbounded_removes_the_permit_and_wakes_waiters()
    {
        var projectId = $"unbounded-project-{Guid.NewGuid():N}";
        var agentId = $"unbounded-agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var gate = _fixture.Grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));

        Assert.Equal(AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, "running", "job-running", AgentConcurrencyPermitOwnerKind.Job));
        Assert.Equal(AgentConcurrencyAcquireResult.Waiting,
            await gate.AcquireAsync(projectId, agentId, "waiting", "job-waiting", AgentConcurrencyPermitOwnerKind.Job));

        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: null);
        await gate.ReleaseAsync(projectId, agentId, "running");

        Assert.Equal(0, await gate.GetActiveCountAsync());
        Assert.Empty(await gate.GetWaitersAsync());
    }

    private static Task RemindAsync(IAgentConcurrencyGrain gate, DateTimeOffset now) =>
        gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now.UtcDateTime, TimeSpan.FromSeconds(30), now.UtcDateTime));
}
