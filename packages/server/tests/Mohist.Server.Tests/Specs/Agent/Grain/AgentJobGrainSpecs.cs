using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Specs.Agent.Grain;

[Collection("WorkflowGrain")]
public class AgentJobGrainSpecs : WorkflowGrainSpecs
{
    public AgentJobGrainSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private IAgentJobGrain JobGrain(string key) => Grains.GetGrain<IAgentJobGrain>(key);

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        TimeSpan step,
        string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        T last = default!;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await probe();
            if (done(last)) return last;
            await Task.Delay(step);
        }
        Assert.Fail($"Timed out waiting for: {description}. Last value: {last}");
        return last;
    }

    private static async Task WaitForStatusAsync(IAgentJobGrain job, AgentJobStatus expected, TimeSpan timeout)
    {
        await WaitForAsync(
            () => job.GetStatusAsync(),
            s => s == expected,
            timeout,
            TimeSpan.FromMilliseconds(25),
            $"status == {expected}");
    }

    private async Task<(string RunnerId, string ProjectId)> RegisterAgentJobRunnerAsync(
        string runnerId,
        string? projectId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        var pid = projectId ?? $"agent-job-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "agent-job-host",
            pid,
            MaxWorkflowSlots: maxWorkflowSlots));
        return (runnerId, pid);
    }

    private static AgentJobInput MakeInput(string prompt, string projectId, string workspacePath = "/tmp/agent-job") =>
        new(Prompt: prompt, WorkspacePath: workspacePath, ProjectId: projectId);

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task SubmitAsync_TransitionsPendingToRunning_WhenRunnerAcceptsDispatch()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-dispatch-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-dispatch-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "Hello world",
            Model: "test/model",
            WorkspacePath: "/tmp/agent-job-test",
            ProjectId: projectId));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CurrentWorkId));
        Assert.Null(snapshot.FailureReason);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        Assert.Contains(jobKey, state.ActiveWorks.Select(w => w.OwnerId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ReportResultAsync_TransitionsRunningToCompleted_OnSuccess()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-success-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-success-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("do the thing", projectId, "/tmp/agent-job-success"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var snapshot = await job.GetRuntimeSnapshotAsync();
        var workId = snapshot.CurrentWorkId!;
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var report = await runner.ReportResultAsync(
            dispatch,
            workId,
            new WorkResult("completed", "ok", Output: "{}", ExitCode: 0, ArtifactUploadIds: ["artifact-1"]));
        Assert.True(report.Tracked);

        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, terminal.Status);
        Assert.Equal("ok", terminal.Message);
        Assert.Equal("{}", terminal.Output);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal(new[] { "artifact-1" }, terminal.ArtifactUploadIds);
        Assert.Null(terminal.FailureReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ReportResultAsync_TransitionsRunningToFailed_OnFailure()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-fail-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-fail-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("do a failing thing", projectId, "/tmp/agent-job-fail"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var snapshot = await job.GetRuntimeSnapshotAsync();
        var workId = snapshot.CurrentWorkId!;
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var report = await runner.ReportResultAsync(
            dispatch,
            workId,
            new WorkResult("failed", "boom", Output: "{\"error\":\"x\"}", ExitCode: 1));
        Assert.True(report.Tracked);

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal("boom", terminal.Message);
        Assert.Equal("{\"error\":\"x\"}", terminal.Output);
        Assert.Equal(1, terminal.ExitCode);
        Assert.Equal("boom", terminal.FailureReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task ReportResultAsync_AfterTerminalCompletion_IsRejected_AndPriorResultPreserved()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-terminal-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-terminal-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("first", projectId, "/tmp/agent-job-terminal"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.ReportResultAsync(dispatch, workId, new WorkResult("completed", "first result"));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var firstTerminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, firstTerminal.Status);
        Assert.Equal("first result", firstTerminal.Message);

        await runner.ReportResultAsync(
            dispatch,
            workId,
            new WorkResult("failed", "second result"));

        var stillTerminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, stillTerminal.Status);
        Assert.Equal("first result", stillTerminal.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task SubmitAsync_NoEligibleRunner_StaysPendingAndRetriesWithBackoff()
    {
        var jobKey = $"agent-job-no-slot-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("no slot", $"agent-job-missing-project-{Guid.NewGuid():N}", "/tmp/agent-job-no-slot"));

        var stillPending = await WaitForAsync(
            () => job.GetStatusAsync(),
            s => s == AgentJobStatus.Pending,
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(25),
            "job stays pending past first attempt");
        Assert.Equal(AgentJobStatus.Pending, stillPending);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task SubmitAsync_BoundExceeded_TransitionsToFailedWithRunnerUnavailable()
    {
        var jobKey = $"agent-job-bound-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("no runner ever", $"agent-job-missing-project-bound-{Guid.NewGuid():N}", "/tmp/agent-job-bound"));

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(8));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, terminal.FailureReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task SubmitAsync_DoesNotCallIWorkflowBacklogGrain()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-bypass-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-bypass-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("should bypass backlog", projectId, "/tmp/agent-job-bypass"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(projectId));
        var claimed = await backlog.ClaimAsync(runnerId);
        Assert.Null(claimed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task RunningJob_WithoutReport_JobTimeout_TransitionsToFailed()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-timeout-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-timeout-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("never reports", projectId, "/tmp/agent-job-timeout"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(15));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.ReportTimeout, terminal.FailureReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task GetGrain_IAgentJobGrain_ResolvesActiveActivation()
    {
        var jobKey = $"agent-job-resolve-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);

        var status = await job.GetStatusAsync();
        Assert.Equal(AgentJobStatus.Pending, status);

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, snapshot.Status);
        Assert.Null(snapshot.RunnerId);
        Assert.Null(snapshot.CurrentWorkId);

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Pending, terminal.Status);
        Assert.Null(terminal.FailureReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task SubmitAsync_SecondCallAfterRunning_ThrowsInvalidOperationException()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-resubmit-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-resubmit-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("first", projectId, "/tmp/agent-job-resubmit"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await job.SubmitAsync(MakeInput("second", projectId, "/tmp/agent-job-resubmit")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task SubmitAsync_NoEligibleRunner_IncrementsDispatchAttemptsAcrossRetries()
    {
        var jobKey = $"agent-job-retry-attempts-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("retry attempts", $"missing-project-{Guid.NewGuid():N}", "/tmp/agent-job-retry"));

        var snapshot = await WaitForAsync(
            () => job.GetRuntimeSnapshotAsync(),
            s => s.DispatchAttempts >= 2,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(25),
            "dispatch attempts >= 2");

        Assert.Equal(AgentJobStatus.Pending, snapshot.Status);
        Assert.True(snapshot.DispatchAttempts >= 2);
    }
}

[Collection("WorkflowGrain")]
public class AgentJobOptionsBindingSpecs
{
    private readonly WorkflowGrainFixture _fixture;

    public AgentJobOptionsBindingSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task AgentJobOptions_ResolveBackoffSchedule_UsesDefaultsFromConfigurationKnob()
    {
        var provider = _fixture.Cluster.GetSiloServiceProvider(null);
        var optionsAccessor = provider.GetRequiredService<IOptions<AgentJobOptions>>();
        var schedule = optionsAccessor.Value.ResolveBackoffSchedule();

        Assert.Equal(TimeSpan.FromMilliseconds(50), schedule.Initial);
        Assert.Equal(TimeSpan.FromMilliseconds(200), schedule.Cap);
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.TotalBound);
        Assert.Equal(TimeSpan.FromSeconds(10), optionsAccessor.Value.JobTimeout);

        var next = schedule.NextDelay(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(50), next);

        var capped = schedule.NextDelay(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), capped);
    }
}
