using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

/// <summary>
/// Shared-admission coverage for workflow-originated AgentJobs (issue 559,
/// T-002 / design D4). The jobs are driven directly through the manual-launch
/// entry points carrying the <see cref="AgentJobWorkflowInvocation"/>
/// discriminator, the frozen per-invocation deadline, and the frozen task
/// expect — exactly the shape <c>WorkflowAgentHandoffGrain</c> activation
/// materializes. These specs prove the workflow-originated job crosses the
/// unchanged admission boundary (per-Agent concurrency permits, runner slot
/// election, ledger admission, poll-time claim) with no second queue or
/// scheduler, and that <c>ArmJobTimeout</c> honors the per-invocation
/// deadline over the global <see cref="AgentJobOptions.JobTimeout"/>
/// backstop while direct launches keep the global bound.
/// </summary>
[Collection("AgentJobGrain")]
public class WorkflowAgentJobAdmissionSpecs : AgentJobGrainTestSupport
{
    private const string FrozenExpect = "{\"files\":[\"plans/agent.md\"],\"failIf\":[\"marker:boom\"]}";

    public WorkflowAgentJobAdmissionSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task WorkflowOriginatedJob_AtAgentConcurrencyLimit_WaitsUnderTheSharedGate_AndIsLaterAdmittedLikeADirectLaunch()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-agent-limit-runner-{Guid.NewGuid():N}");
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);

        // A direct launch occupies the single per-Agent permit.
        var direct = JobGrain($"agent-job-wf-limit-direct-{Guid.NewGuid():N}");
        await direct.SubmitAsync(MakeInput("occupy the shared permit", projectId));
        await WaitForStatusAsync(direct, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        // The workflow-originated job waits under the same per-Agent gate.
        var workflowRunId = $"workflow-run-{Guid.NewGuid():N}";
        var taskRunId = $"task-run-{Guid.NewGuid():N}";
        var workflow = await LaunchWorkflowOriginatedJobAsync(
            $"agent-job-wf-limit-{Guid.NewGuid():N}",
            projectId,
            workflowRunId,
            taskRunId,
            timeoutMilliseconds: 60_000);

        var waiting = await WaitForAsync(
            () => LoadJobStateAsync(workflow.GetPrimaryKeyString()),
            state => state.WaitingReason == AgentAvailabilityWaitReasons.CapacityFull,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "workflow-originated job waits on the shared concurrency gate");

        Assert.Equal(AgentJobStatus.Pending, waiting.Status);
        Assert.False(waiting.ConcurrencyPermitHeld);
        Assert.Null(waiting.RunnerId);
        var gate = Grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, "agent-test"));
        var waiters = (await gate.GetSnapshotAsync()).Waiters
            .Where(waiter => waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Job
                && string.Equals(waiter.OwnerId, workflow.GetPrimaryKeyString(), StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(waiters);

        // Releasing the direct launch's permit admits the waiting
        // workflow-originated job through the same path as a direct launch.
        var directSnapshot = await direct.GetRuntimeSnapshotAsync();
        await direct.ReportResultAsync(
            directSnapshot.RunnerId!,
            directSnapshot.CurrentWorkId!,
            new WorkResult("completed"));

        var admitted = await WaitForAsync(
            () => workflow.GetRuntimeSnapshotAsync(),
            snapshot => string.Equals(snapshot.RunnerId, runnerId, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "workflow-originated job admitted after the shared permit frees");
        Assert.Equal(AgentJobStatus.Pending, admitted.Status);
        Assert.False(string.IsNullOrWhiteSpace(admitted.CurrentWorkId));

        // The eligible runner claims it through the existing poll path.
        await Grains.GetGrain<IRunnerGrain>(runnerId)
            .PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.Equal(AgentJobStatus.Running, await workflow.GetStatusAsync());
    }

    [Fact]
    public async Task WorkflowOriginatedJob_WithoutEligibleRunner_RemainsAdmittedButWaiting_AndIsNotFailedByTheWait()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"wf-agent-no-runner-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: null);

        var workflow = await LaunchWorkflowOriginatedJobAsync(
            $"agent-job-wf-no-runner-{Guid.NewGuid():N}",
            projectId,
            $"workflow-run-{Guid.NewGuid():N}",
            $"task-run-{Guid.NewGuid():N}",
            timeoutMilliseconds: 60_000);

        // Advance past the dispatch retry bound and drive the recovery
        // reconciliation the reminder would perform: no eligible runner is
        // online, so the job must stay admitted-but-waiting with its reason.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(6));
        await workflow.CheckTimeoutsAsync();

        var state = await LoadJobStateAsync(workflow.GetPrimaryKeyString());
        Assert.Equal(AgentJobStatus.Pending, state.Status);
        Assert.Equal(AgentAvailabilityWaitReasons.NoOnlineRunner, state.WaitingReason);
        // The wait itself never fails the execution: the Workflow task is
        // not independently failed while its invocation keeps waiting.
        var terminal = await workflow.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Pending, terminal.Status);
        Assert.Null(terminal.FailureReason);
    }

    [Fact]
    public async Task WorkflowOriginatedJob_IsClaimedThroughTheAgentJobLedgerPoll_CarryingLineageAndFrozenExpect()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-agent-claim-runner-{Guid.NewGuid():N}");
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: null);
        var workflowRunId = $"workflow-run-{Guid.NewGuid():N}";
        var taskRunId = $"task-run-{Guid.NewGuid():N}";
        var workflow = await LaunchWorkflowOriginatedJobAsync(
            $"agent-job-wf-claim-{Guid.NewGuid():N}",
            projectId,
            workflowRunId,
            taskRunId,
            timeoutMilliseconds: 60_000,
            expect: FrozenExpect);

        await _fixture.DispatchObserver.WaitForAssignmentPreparedAsync();
        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId)
            .PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));

        // The claim went through the existing agent-job ledger poll
        // (DispatchService → RunnerGrain.TryClaimAgentJobAsync →
        // AgentJobGrain.ClaimNextAsync): the dispatch carries the AgentJob
        // owner kind together with the workflow lineage and frozen expect.
        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled!.OwnerKind);
        Assert.Equal(workflow.GetPrimaryKeyString(), polled.AgentJobId);
        Assert.Equal(workflowRunId, polled.WorkflowRunId);
        Assert.Equal(taskRunId, polled.TaskRunId);
        Assert.Equal(FrozenExpect, polled.Expect);
        Assert.Equal(AgentJobStatus.Running, await workflow.GetStatusAsync());

        // A later poll redelivers the same immutable dispatch snapshot from
        // the ledger row, so the lineage and expect survive re-delivery.
        var redelivered = await Grains.GetGrain<IRunnerGrain>(runnerId)
            .PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(redelivered);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, redelivered!.OwnerKind);
        Assert.Equal(workflow.GetPrimaryKeyString(), redelivered.AgentJobId);
        Assert.Equal(workflowRunId, redelivered.WorkflowRunId);
        Assert.Equal(taskRunId, redelivered.TaskRunId);
        Assert.Equal(FrozenExpect, redelivered.Expect);

        var snapshot = await workflow.GetRuntimeSnapshotAsync();
        await workflow.ReportResultAsync(
            snapshot.RunnerId!,
            snapshot.CurrentWorkId!,
            new WorkResult("completed"));
    }

    [Fact]
    public async Task WorkflowOriginatedJob_WithExplicitShortTimeout_BoundsExecutionExactlyAsDeclared()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-agent-short-runner-{Guid.NewGuid():N}");
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: null);
        var workflow = await LaunchWorkflowOriginatedJobAsync(
            $"agent-job-wf-short-{Guid.NewGuid():N}",
            projectId,
            $"workflow-run-{Guid.NewGuid():N}",
            $"task-run-{Guid.NewGuid():N}",
            timeoutMilliseconds: 5_000);

        await WaitForStatusAsync(workflow, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        // Before the declared 5s deadline (and before the 10s global
        // backstop) the execution is still running.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(4));
        await workflow.CheckTimeoutsAsync();
        Assert.Equal(AgentJobStatus.Running, await workflow.GetStatusAsync());

        // Past the declared deadline the execution is bounded exactly as
        // declared, even though the global backstop is 10s.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        await workflow.CheckTimeoutsAsync();

        var terminal = await workflow.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Unknown, terminal.Status);
        Assert.StartsWith(AgentJobFailureReasons.ReportTimeout, terminal.FailureReason, StringComparison.Ordinal);
        Assert.Contains(TimeSpan.FromSeconds(5).ToString(), terminal.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowOriginatedJob_WithSixtyMinuteDefaultDeadline_IsNotFailedByTheGlobalBackstop()
    {
        // The handoff resolves an omitted task timeout to the runtime
        // action default (60 minutes) — the same value the activation
        // specs freeze onto AgentJobInput. The manual launch here carries
        // that resolved deadline verbatim.
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-agent-default-runner-{Guid.NewGuid():N}");
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: null);
        var workflow = await LaunchWorkflowOriginatedJobAsync(
            $"agent-job-wf-default-{Guid.NewGuid():N}",
            projectId,
            $"workflow-run-{Guid.NewGuid():N}",
            $"task-run-{Guid.NewGuid():N}",
            timeoutMilliseconds: WorkflowAgentHandoffDeadline.DefaultTimeoutMilliseconds);

        await WaitForStatusAsync(workflow, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        // Advance past the 10s global JobTimeout backstop: the per-invocation
        // deadline overrides it, so the long agent turn keeps running.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await workflow.CheckTimeoutsAsync();

        Assert.Equal(AgentJobStatus.Running, await workflow.GetStatusAsync());
        var terminal = await workflow.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Running, terminal.Status);
        Assert.Null(terminal.FailureReason);

        var snapshot = await workflow.GetRuntimeSnapshotAsync();
        await workflow.ReportResultAsync(
            snapshot.RunnerId!,
            snapshot.CurrentWorkId!,
            new WorkResult("completed"));
        await WaitForStatusAsync(workflow, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DirectLaunchJob_WithoutPerInvocationDeadline_KeepsTheGlobalBackstop()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"wf-agent-backstop-runner-{Guid.NewGuid():N}");
        var direct = JobGrain($"agent-job-wf-backstop-direct-{Guid.NewGuid():N}");

        await direct.SubmitAsync(MakeInput("keep the global backstop", projectId));
        await WaitForStatusAsync(direct, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        // A direct launch carries no per-invocation deadline: the 10s global
        // backstop still bounds it (unchanged admission/timeout behavior).
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await direct.CheckTimeoutsAsync();

        var terminal = await direct.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Unknown, terminal.Status);
        Assert.StartsWith(AgentJobFailureReasons.ReportTimeout, terminal.FailureReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Materializes a workflow-originated AgentJob through the manual-launch
    /// entry points with the workflow discriminator, the frozen deadline, and
    /// the frozen expect — the exact participant shape the handoff
    /// activation produces (PrepareJob + EnsureInitialLaunch + SubmitJob).
    /// </summary>
    private async Task<IAgentJobGrain> LaunchWorkflowOriginatedJobAsync(
        string jobKey,
        string projectId,
        string workflowRunId,
        string taskRunId,
        long? timeoutMilliseconds,
        string? expect = FrozenExpect)
    {
        var job = JobGrain(jobKey);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: $"agent-session-wf-{jobKey}",
            InputId: $"workflow-agent-input-{jobKey}",
            TurnId: $"workflow-agent-turn-{jobKey}",
            Prompt: "run the workflow agent task",
            ProjectId: projectId,
            AgentId: "agent-test",
            WorkflowRunId: workflowRunId,
            Skills: [],
            WorkflowInvocation: new AgentJobWorkflowInvocation(
                InvocationId: $"workflow-agent-invocation-{jobKey}",
                TaskRunId: taskRunId,
                WorkId: $"workflow-work-{jobKey}"),
            TimeoutMilliseconds: timeoutMilliseconds,
            Expect: expect));
        await job.SubmitPreparedLaunchAsync();
        return job;
    }

    private async Task<AgentJobState> LoadJobStateAsync(string jobKey)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var ledger = await jobs.LoadLedgerAsync(jobKey);
        Assert.NotNull(ledger);
        return JSON.Deserialize<AgentJobState>(ledger!.StateJson)!;
    }
}
