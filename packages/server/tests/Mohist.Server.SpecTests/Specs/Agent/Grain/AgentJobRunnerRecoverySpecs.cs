using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentJobRunnerRecoverySpecs : AgentJobGrainTestSupport
{
    public AgentJobRunnerRecoverySpecs(AgentJobGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerRestart_RedeliversSameWork_AndTerminalReportIsIdempotent()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-recovery-runner",
            projectId: "agent-job-recovery-project");
        var jobKey = "agent-job-recovery";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("recover after runner restart", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var beforeRestart = await job.GetRuntimeSnapshotAsync();
        var originalWorkId = beforeRestart.CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            projectId));

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        var recovered = Assert.Single(
            (await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        Assert.Equal(jobKey, recovered.AgentJobId);
        Assert.Equal(originalWorkId, recovered.WorkId);

        var firstReport = await job.ReportResultAsync(
            runnerId,
            originalWorkId,
            new WorkResult("completed", "recovered"));
        var duplicateReport = await job.ReportResultAsync(
            runnerId,
            originalWorkId,
            new WorkResult("failed", "stale duplicate"));

        Assert.True(firstReport.Accepted);
        Assert.False(duplicateReport.Accepted);
        Assert.Equal("stale", duplicateReport.Reason);
        Assert.Equal(AgentJobStatus.Completed, (await job.GetTerminalResultAsync()).Status);
        await runner.UnregisterAsync();
    }

    [Fact]
    public async Task RunnerRestart_StartedFenceObservation_EntersUnknownWithoutTerminalFailure()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-recovery-unknown-runner",
            projectId: "agent-job-recovery-unknown-project");
        var jobKey = "agent-job-recovery-unknown";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("preserve unknown after runner restart", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();
        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());

        var unknown = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult(
                "unknown",
                "Runner restarted after a durable started fence without a completed result receipt."));

        Assert.True(unknown.Accepted);
        Assert.Equal("unknown", unknown.Reason);
        var unknownResult = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Unknown, unknownResult.Status);
        Assert.Contains("durable started fence", unknownResult.FailureReason, StringComparison.Ordinal);

        var duplicateUnknown = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("unknown", "same observation"));
        Assert.True(duplicateUnknown.Accepted);
        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());

        var terminal = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("completed", "authoritative result after reconciliation"));
        Assert.True(terminal.Accepted);
        Assert.Equal(AgentJobStatus.Completed, await job.GetStatusAsync());
        await runner.UnregisterAsync();
    }

    [Fact]
    public async Task RunnerRestart_PreservesEveryActiveAgentWorkWithoutPartialTerminalization()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            "agent-job-recovery-many-runner",
            projectId: "agent-job-recovery-project",
            maxWorkflowSlots: 2);
        var firstJob = JobGrain("agent-job-recovery-first");
        var secondJob = JobGrain("agent-job-recovery-second");

        await firstJob.SubmitAsync(MakeInput("recover first work", projectId));
        await secondJob.SubmitAsync(MakeInput("recover second work", projectId));
        await WaitForStatusAsync(firstJob, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        await WaitForStatusAsync(secondJob, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var firstWorkId = (await firstJob.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var secondWorkId = (await secondJob.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal(AgentJobStatus.Running, await firstJob.GetStatusAsync());
        Assert.Equal(AgentJobStatus.Running, await secondJob.GetStatusAsync());

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            projectId));

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>()
            .CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DispatchService>();
        var recovered = (await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches;

        Assert.Equal(2, recovered.Count);
        Assert.Equal(
            new[] { firstWorkId, secondWorkId }.Order(),
            recovered.Select(work => work.WorkId).Order());

        var firstReport = await firstJob.ReportResultAsync(
            runnerId,
            firstWorkId,
            new WorkResult("completed", "first recovered work"));
        var secondReport = await secondJob.ReportResultAsync(
            runnerId,
            secondWorkId,
            new WorkResult("completed", "second recovered work"));

        Assert.True(firstReport.Accepted);
        Assert.True(secondReport.Accepted);
        Assert.Equal(AgentJobStatus.Completed, await firstJob.GetStatusAsync());
        Assert.Equal(AgentJobStatus.Completed, await secondJob.GetStatusAsync());
        await runner.UnregisterAsync();
    }
}
