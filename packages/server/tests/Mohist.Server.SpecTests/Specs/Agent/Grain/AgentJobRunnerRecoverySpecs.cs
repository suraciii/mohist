using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentJobRunnerRecoverySpecs : AgentJobGrainTestSupport
{
    public AgentJobRunnerRecoverySpecs(AgentJobGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerLoss_FailsClaimedWorkUnderItsProcessGeneration()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-runner-loss-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-runner-loss-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("fail when the owning process is lost", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var beforeLoss = await job.GetRuntimeSnapshotAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, terminal.FailureReason);
        var afterLoss = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(beforeLoss.CurrentWorkId, afterLoss.CurrentWorkId);
        Assert.False(afterLoss.IsRecovering);
        Assert.Null(afterLoss.RecoveryDeadlineAt);
    }

    [Fact]
    public async Task ReplacementProcess_DoesNotRedeliverOrAcceptOldGenerationWork()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-replacement-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-replacement-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("do not replay across process generations", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.UnregisterAsync();
        await runner.RegisterAsync(
            new RunnerInfo(runnerId, ["spec/*"], "replacement-host", projectId),
            "replacement-generation");

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        var replacementPoll = await dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: "replacement-generation"));
        Assert.Empty(replacementPoll.Dispatches);

        var late = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("completed", "late result from the replaced process"));
        Assert.False(late.Accepted);
        Assert.Equal("refused", late.Reason);
        Assert.Equal(AgentJobStatus.Failed, await job.GetStatusAsync());
    }
}
