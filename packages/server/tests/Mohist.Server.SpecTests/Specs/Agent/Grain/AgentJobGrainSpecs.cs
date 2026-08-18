using Mohist.Server.Infrastructure;
using Mohist.Server.Contracts;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentJobGrainSpecs : AgentJobGrainTestSupport
{
    public AgentJobGrainSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task SubmitAsync_WithoutAgentIdentity_IsRejectedBeforeDispatch()
    {
        var job = JobGrain($"agent-job-missing-agent-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            job.SubmitAsync(new AgentJobInput(Prompt: "missing identity")));

        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());
    }

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
            ProjectId: projectId,
            AgentId: "agent-test"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runnerId, snapshot.RunnerId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CurrentWorkId));
        Assert.Null(snapshot.FailureReason);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        Assert.Contains(jobKey, state.ActiveWorks.Select(w => w.OwnerId));
    }

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

        var report = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("completed", "ok", Output: JSON.DeserializeElement("{}"), ExitCode: 0, ArtifactUploadIds: ["artifact-1"]));
        Assert.True(report.Accepted);

        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, terminal.Status);
        Assert.Equal("ok", terminal.Message);
        Assert.Equal("{}", terminal.Output);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal(new[] { "artifact-1" }, terminal.ArtifactUploadIds);
        Assert.Null(terminal.FailureReason);

        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var ownership = await db.TerminalLogOwnerships.SingleAsync(row =>
            row.OwnerKind == TerminalLogOwnerKinds.AgentJob
            && row.OwnerId == jobKey
            && row.WorkId == workId);
        Assert.Equal(runnerId, ownership.RunnerId);
    }

    [Fact]
    public async Task ReportResultAsync_AdmittedPendingJob_IsRejectedWithoutChangingLedgerState()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-pending-report-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-pending-report-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("must be claimed first", projectId));
        var assigned = await WaitForAsync(
            () => job.GetRuntimeSnapshotAsync(),
            snapshot => snapshot.Status == AgentJobStatus.Pending
                && snapshot.RunnerId == runnerId
                && !string.IsNullOrWhiteSpace(snapshot.CurrentWorkId),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "pending AgentJob assignment");

        var report = await job.ReportResultAsync(
            runnerId,
            assigned.CurrentWorkId!,
            new WorkResult("completed", "must not complete"));

        Assert.False(report.Accepted);
        Assert.Equal("not-running", report.Reason);
        var after = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Pending, after.Status);
        Assert.Equal(assigned.RunnerId, after.RunnerId);
        Assert.Equal(assigned.CurrentWorkId, after.CurrentWorkId);
    }

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

        var report = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("failed", "boom", Output: JSON.DeserializeElement("{\"error\":\"x\"}"), ExitCode: 1));
        Assert.True(report.Accepted);

        await WaitForStatusAsync(job, AgentJobStatus.Failed, TimeSpan.FromSeconds(5));

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal("boom", terminal.Message);
        Assert.Equal("{\"error\":\"x\"}", terminal.Output);
        Assert.Equal(1, terminal.ExitCode);
        Assert.Equal("boom", terminal.FailureReason);
    }

    [Fact]
    public async Task SessionInterruptionDeliveryFailure_IsRepairedByIdempotentOwnerRetry()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-session-repair-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-session-repair-{Guid.NewGuid():N}";
        var sessionId = $"agent-session-session-repair-{Guid.NewGuid():N}";
        var inputId = $"input-session-repair-{Guid.NewGuid():N}";
        var turnId = $"turn-session-repair-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            "repair AgentJob session visibility",
            "agent-job",
            jobKey,
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mohist.io/project-id"] = projectId,
                ["mohist.io/source-kind"] = "agent-launch",
                ["mohist.io/agent-id"] = "agent-test",
            }),
            Runtime: "opencode"));

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            "repair AgentJob session visibility",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-test",
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId,
            PinnedRunnerId: runnerId));
        await WaitForRunningAsync(job);
        var running = await job.GetRuntimeSnapshotAsync();
        var workId = running.CurrentWorkId!;
        var operationId = $"runner-update:agent-job-session-repair-{Guid.NewGuid():N}";
        Assert.True(await job.RecordRuntimeSessionBindingAsync(
            runnerId,
            workId,
            sessionId,
            $"runtime-session-{Guid.NewGuid():N}"));

        _fixture.SessionStatePersistence.QueueFailures(1);
        await Assert.ThrowsAnyAsync<Exception>(() => job.MarkUpdateInterruptedAsync(
            runnerId,
            workId,
            operationId));

        Assert.Equal(AgentJobStatus.RecoverablyInterrupted, await job.GetStatusAsync());
        _fixture.SessionStatePersistence.Reset();
        Assert.True(await job.MarkUpdateInterruptedAsync(runnerId, workId, operationId));

        var repaired = await session.GetAsync();
        Assert.NotNull(repaired);
        Assert.Equal(AgentWorkInterruptionStates.Interrupted,
            Assert.Single(repaired!.InterruptionHistory!).State);
        var repairedTurn = Assert.Single(await session.ListTurnsAsync());
        Assert.Equal(AgentWorkInterruptionStates.Interrupted, repairedTurn.Interruption?.State);
    }

    [Fact]
    public async Task MissingSessionRetainsVisibilityDeliveryUntilSessionMaterializes()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-missing-session-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-missing-session-{Guid.NewGuid():N}";
        var sessionId = $"missing-session-{Guid.NewGuid():N}";
        var inputId = $"input-missing-session-{Guid.NewGuid():N}";
        var turnId = $"turn-missing-session-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);

        await job.SubmitAsync(new AgentJobInput(
            "repair missing AgentJob session visibility",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-test",
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId,
            PinnedRunnerId: runnerId));
        await WaitForRunningAsync(job);
        var running = await job.GetRuntimeSnapshotAsync();
        var workId = running.CurrentWorkId!;
        var operationId = $"runner-update:missing-session-{Guid.NewGuid():N}";

        Assert.Null(await session.GetAsync());
        Assert.True(await job.MarkUpdateInterruptedAsync(runnerId, workId, operationId));

        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await job.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);

        var repaired = await session.GetAsync();
        Assert.NotNull(repaired);
        Assert.Contains(repaired!.InterruptionHistory!, transition =>
            transition.State == AgentWorkInterruptionStates.Interrupted
            && transition.UpdateOperationId == operationId);
    }

    [Fact]
    public async Task RecoveryTerminalReplayRepairsOperationAfterOwnerCommitWriteFailure()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-recovery-terminal-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-recovery-terminal-{Guid.NewGuid():N}";
        var sessionId = $"agent-session-recovery-terminal-{Guid.NewGuid():N}";
        var inputId = $"input-recovery-terminal-{Guid.NewGuid():N}";
        var turnId = $"turn-recovery-terminal-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-session-recovery-terminal-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: runnerId,
            AgentRuntime: "opencode",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mohist.io/project-id"] = projectId,
                ["mohist.io/source-kind"] = "agent-launch",
                ["mohist.io/agent-id"] = "agent-test",
            })));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            "recover this job",
            "agent-job",
            jobKey,
            Runtime: "opencode"));

        var job = JobGrain(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            "recover this job",
            ProjectId: projectId,
            Runtime: "opencode",
            AgentId: "agent-test",
            AgentSessionId: sessionId,
            InitialInputId: inputId,
            InitialTurnId: turnId,
            PinnedRunnerId: runnerId));
        await WaitForRunningAsync(job);
        var initial = await job.GetRuntimeSnapshotAsync();
        Assert.True(await job.RecordRuntimeSessionBindingAsync(
            runnerId,
            initial.CurrentWorkId!,
            sessionId,
            runtimeSessionId));

        var operationId = $"update-recovery-terminal-{Guid.NewGuid():N}";
        var operationGrain = Grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            runnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new List<RunnerUpdateWork>
            {
                new(
                    WorkDispatchOwnerKinds.AgentJob,
                    jobKey,
                    initial.CurrentWorkId!,
                    null,
                    "agent-job"),
            }));
        Assert.True(await job.MarkUpdateInterruptedAsync(
            runnerId,
            initial.CurrentWorkId!,
            operationId));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.AgentJob,
            jobKey,
            initial.CurrentWorkId!,
            taskRunId: null,
            RunnerUpdateWorkStatus.Marked);

        var interruptedReceipt = new RuntimeRecoveryReceipt(
            WorkflowRunId: string.Empty,
            TaskRunId: string.Empty,
            WorkId: initial.CurrentWorkId!,
            RunnerId: runnerId,
            AgentSessionId: sessionId,
            AgentTurnId: turnId,
            Runtime: "opencode",
            RuntimeSessionId: runtimeSessionId,
            RecoveryGeneration: 0,
            ReceiptId: "agent-job-recovery-terminal-interrupted",
            Payload: new RuntimeRecoveryReceiptPayload(
                RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted,
                UpdateOperationId: operationId,
                StopConfirmed: true),
            OwnerKind: RuntimeRecoveryReceiptOwnerKinds.AgentJob,
            AgentJobId: jobKey);
        var interruptedAcknowledgement = await job.ReceiveRecoveryReceiptAsync(interruptedReceipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, interruptedAcknowledgement.Status);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var replacement = await runner.TryClaimAgentJobAsync(jobKey, projectId);
        Assert.NotNull(replacement);
        var replacementRuntimeSessionId = $"runtime-session-recovery-terminal-replacement-{Guid.NewGuid():N}";
        Assert.True(await job.RecordRuntimeSessionBindingAsync(
            runnerId,
            replacement!.WorkId,
            sessionId,
            replacementRuntimeSessionId));
        var replacementSnapshot = await job.GetRuntimeSnapshotAsync();
        var result = new WorkResult("completed", "recovery terminal result");
        var terminalReceipt = new RuntimeRecoveryReceipt(
            WorkflowRunId: string.Empty,
            TaskRunId: string.Empty,
            WorkId: replacement.WorkId,
            RunnerId: runnerId,
            AgentSessionId: sessionId,
            AgentTurnId: replacementSnapshot.InitialTurnId!,
            Runtime: "opencode",
            RuntimeSessionId: replacementRuntimeSessionId,
            RecoveryGeneration: replacementSnapshot.RecoveryGeneration,
            ReceiptId: "agent-job-recovery-terminal-result",
            Payload: new RuntimeRecoveryReceiptPayload(
                RuntimeRecoveryReceiptPayloadTypes.TerminalResult,
                Result: result,
                Fingerprint: RuntimeRecoveryReceiptFingerprint.For(result)),
            OwnerKind: RuntimeRecoveryReceiptOwnerKinds.AgentJob,
            AgentJobId: jobKey);

        _fixture.OperationWriteFailures.FailNext(RunnerUpdateOperationWriteKind.MarkRecoverySettled);
        await Assert.ThrowsAnyAsync<Exception>(() => job.ReceiveRecoveryReceiptAsync(terminalReceipt));

        Assert.Equal(AgentJobStatus.Completed, await job.GetStatusAsync());
        var beforeReplay = await operationGrain.GetAsync(operationId);
        Assert.Equal(
            RunnerUpdateRecoveryStatus.ReceiptAcked,
            Assert.Single(beforeReplay!.AffectedWorks).RecoveryStatus);

        var replayAcknowledgement = await job.ReceiveRecoveryReceiptAsync(terminalReceipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, replayAcknowledgement.Status);
        var settled = await operationGrain.GetAsync(operationId);
        Assert.Equal(
            RunnerUpdateRecoveryStatus.ReplacementSettled,
            Assert.Single(settled!.AffectedWorks).RecoveryStatus);
    }

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

        await job.ReportResultAsync(runnerId, workId, new WorkResult("completed", "first result"));
        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));

        var firstTerminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, firstTerminal.Status);
        Assert.Equal("first result", firstTerminal.Message);

        var replay = await job.ReportResultAsync(
            runnerId,
            workId,
            new WorkResult("failed", "second result"));
        Assert.False(replay.Accepted);
        Assert.Equal("stale", replay.Reason);

        var stillTerminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Completed, stillTerminal.Status);
        Assert.Equal("first result", stillTerminal.Message);
    }

    [Fact]
    public async Task SubmitAsync_NoEligibleRunner_StaysPendingAndRetriesWithBackoff()
    {
        await ClearGlobalRunnerRegistryAsync();

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

    [Fact]
    public async Task SubmitAsync_NoEligibleRunner_DoesNotCountPendingJobAgainstConcurrencyLimit()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-missing-project-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);
        var job = JobGrain($"agent-job-no-runner-limit-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("no runner", projectId));

        var gate = Grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, "agent-test"));
        Assert.Equal(0, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task RunningJob_PermitSurvivesConcurrencyReconciliation()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-reconcile-runner-{Guid.NewGuid():N}");
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);
        var jobKey = $"agent-job-reconcile-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("running", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var gate = Grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, "agent-test"));
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now, TimeSpan.FromSeconds(30), now));

        Assert.Equal(1, await gate.GetActiveCountAsync());
        var snapshot = await job.GetRuntimeSnapshotAsync();
        await job.ReportResultAsync(
            runnerId,
            snapshot.CurrentWorkId!,
            new WorkResult("completed"));
    }

    [Fact]
    public async Task SubmitAsync_RunnerAtPersistedSlots_LeavesSecondAgentJobPending()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-capacity-runner-{Guid.NewGuid():N}");
        var firstJobKey = $"agent-job-capacity-first-{Guid.NewGuid():N}";
        var secondJobKey = $"agent-job-capacity-second-{Guid.NewGuid():N}";
        var firstJob = JobGrain(firstJobKey);
        var secondJob = JobGrain(secondJobKey);

        await firstJob.SubmitAsync(MakeInput("occupy slot", projectId, "/tmp/agent-job-capacity-first"));
        await WaitForStatusAsync(firstJob, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await secondJob.SubmitAsync(MakeInput("no capacity", projectId, "/tmp/agent-job-capacity-second"));

        await WaitForStatusAsync(secondJob, AgentJobStatus.Pending, TimeSpan.FromSeconds(5));

        var secondSnapshot = await secondJob.GetRuntimeSnapshotAsync();
        Assert.Null(secondSnapshot.RunnerId);
        Assert.Null(secondSnapshot.CurrentWorkId);

        var secondTerminal = await secondJob.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Pending, secondTerminal.Status);
        Assert.Null(secondTerminal.FailureReason);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var activeWorks = (await runner.GetRuntimeStateAsync()).ActiveWorks;
        Assert.Single(activeWorks, w => w.OwnerKind == WorkDispatchOwnerKinds.AgentJob);
        Assert.Contains(activeWorks, w => w.OwnerId == firstJobKey);
        Assert.DoesNotContain(activeWorks, w => w.OwnerId == secondJobKey);
    }

    [Fact]
    public async Task SubmitAsync_BoundExceeded_StaysPendingWithoutRunnerUnavailableFailure()
    {
        await ClearGlobalRunnerRegistryAsync();

        var job = JobGrain($"agent-job-bound-{Guid.NewGuid():N}");
        await job.SubmitAsync(MakeInput("no runner ever", $"agent-job-missing-project-bound-{Guid.NewGuid():N}", "/tmp/agent-job-bound"));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(6));
        await job.CheckTimeoutsAsync();

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Pending, terminal.Status);
        Assert.NotEqual(AgentJobFailureReasons.RunnerUnavailable, terminal.FailureReason);
    }

    [Fact]
    public async Task SubmitAsync_GenericSession_NoEligibleRunner_StaysPendingAndKeepsSessionOpen()
    {
        await ClearGlobalRunnerRegistryAsync();

        var projectId = $"agent-job-missing-project-bound-{Guid.NewGuid():N}";
        var sessionId = $"generic-session-{Guid.NewGuid():N}";
        var sessionGrain = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await sessionGrain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/generic-session",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));

        var jobKey = $"agent-job-bound-generic-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(new AgentJobInput(
            Prompt: "no runner ever",
            WorkspacePath: "/tmp/agent-job-bound",
            ProjectId: projectId,
            AgentSessionId: sessionId,
            AgentId: "agent-test"));

        // Stay below the configured DispatchRetryBound (5s): a leaked
        // runner from a parallel test can admit this job via the global
        // registry and set ReadySince, after which advancing past the
        // bound would trip the readiness timeout. The no-runner case
        // never sets ReadySince, so the advance only needs to exercise
        // CheckTimeouts without exceeding the bound.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(4));
        await job.CheckTimeoutsAsync();

        // Issue-520 D4: a job with no eligible runner now stays Pending —
        // the dispatch retry bound no longer drives the job into terminal
        // Failed(runner-unavailable). The session keeps its open state
        // and the agent may still receive a runner later.
        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Pending, terminal.Status);
        Assert.NotEqual(AgentJobFailureReasons.RunnerUnavailable, terminal.FailureReason);
    }

    [Fact]
    public async Task SubmitAsync_DoesNotUseWorkflowAssignment()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-bypass-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-bypass-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("should bypass backlog", projectId, "/tmp/agent-job-bypass"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var polled = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(polled);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled.OwnerKind);
        Assert.Equal(jobKey, polled.AgentJobId);
    }

    [Fact]
    public async Task RunningJob_WithoutReport_JobTimeout_TransitionsToUnknown()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-timeout-runner-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-timeout-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("never reports", projectId, "/tmp/agent-job-timeout"));

        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await job.CheckTimeoutsAsync();

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Unknown, terminal.Status);
        Assert.StartsWith(AgentJobFailureReasons.ReportTimeout, terminal.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollClaimedJob_WithoutReport_TransitionsToUnknownAtJobTimeout()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-polled-timeout-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-polled-timeout-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("never reports after poll", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());
    }

    [Fact]
    public async Task ReportTimeout_WhenRunnerIsAway_PreservesRecoveringProjection()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-timeout-away-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-timeout-away-{Guid.NewGuid():N}");

        await job.SubmitAsync(MakeInput("runner disappears before timeout", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.UnregisterAsync();
        var afterCloseout = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, afterCloseout.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, afterCloseout.FailureReason);
        Assert.True(afterCloseout.IsRecovering);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await job.CheckTimeoutsAsync();

        var afterTimeout = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(AgentJobStatus.Unknown, afterTimeout.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerLost, afterTimeout.FailureReason);
        Assert.Equal(afterCloseout.RecoveryDeadlineAt, afterTimeout.RecoveryDeadlineAt);
        Assert.True(afterTimeout.IsRecovering);
    }

    [Fact]
    public async Task DelayedGenericJobFailure_AfterReset_DoesNotCloseTheReplacementRuntime()
    {
        var projectId = $"agent-job-reset-project-{Guid.NewGuid():N}";
        await RegisterAgentJobRunnerAsync("runner-a", projectId);
        var sessionId = $"agent-job-reset-session-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-a",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-reset",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-a"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(10));
        await Grains.GetGrain<IRunnerGrain>("runner-a").TouchPresenceAsync();

        var job = JobGrain($"agent-job-reset-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput("delayed failure", ProjectId: projectId, AgentSessionId: sessionId, AgentId: "agent-test"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await job.RecordRuntimeSessionBindingAsync("runner-a", workId, sessionId, "runtime-a"));
        // Repeat of the same runtimeSessionId is idempotent — the
        // runner may re-report the binding after a reconnect, and
        // the grain must accept it as a no-op (no state mutation, no
        // lineage append). Mismatched values on a repeat report are
        // rejected (#410 T-001 AC).
        Assert.True(await job.RecordRuntimeSessionBindingAsync("runner-a", workId, sessionId, "runtime-a"));
        Assert.False(await job.RecordRuntimeSessionBindingAsync("runner-a", workId, sessionId, "runtime-b"));

        await session.ResetAsync(new ResetAgentSessionCommand("runtime-a", "runtime-b"));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        await job.CheckTimeoutsAsync();

        Assert.Equal("runtime-b", (await session.GetAsync())?.AgentSessionId);
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(turn => turn.SessionId == sessionId)
            .Select(turn => turn.Id)
            .ToListAsync();
        Assert.Empty(await db.AgentSessionTranscriptParts
            .Where(part => turnIds.Contains(part.TurnId) && part.Type == TranscriptPartTypes.SessionActivity)
            .ToListAsync());
    }

    [Fact]
    public async Task AttachedGenericJobFailure_RecordsOneTerminalFactWithRuntimeFailureCategory()
    {
        var projectId = $"agent-job-close-project-{Guid.NewGuid():N}";
        await RegisterAgentJobRunnerAsync("runner-a", projectId);
        var sessionId = $"agent-job-close-session-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: "runner-a",
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-job-close",
            Metadata: new AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = "agent-test",
                })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-a"));

        var job = JobGrain($"agent-job-close-{Guid.NewGuid():N}");
        await job.SubmitAsync(new AgentJobInput("record terminal failure", ProjectId: projectId, AgentSessionId: sessionId, AgentId: "agent-test"));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await job.RecordRuntimeSessionBindingAsync("runner-a", workId, sessionId, "runtime-a"));

        await job.ReportResultAsync("runner-a", workId, new WorkResult(
            "failed",
            "prompt timed out",
            Output: JSON.DeserializeElement("""{"failureCategory":"prompt_timeout"}"""),
            ExitCode: 1));
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var turnIds = await db.AgentSessionTranscriptTurns
            .Where(turn => turn.SessionId == sessionId)
            .Select(turn => turn.Id)
            .ToListAsync();
        var closed = Assert.Single(await db.AgentSessionTranscriptParts
            .Where(part => turnIds.Contains(part.TurnId) && part.Type == TranscriptPartTypes.SessionActivity)
            .ToListAsync());

        Assert.Equal(1, closed.RawEventCount);
        using var payload = JsonDocument.Parse(closed.PayloadJson);
        // Issue 484: terminal delivery now writes exactly one
        // session.activity (activity=idle) part. The runtime failure
        // category is the AgentJob's own verdict and is no longer
        // mirrored onto the session transcript; the work result status
        // remains observable on the part payload.
        Assert.Equal("failed", payload.RootElement.GetProperty("status").GetString());
    }

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

    [Fact]
    public async Task SubmitAsync_IdenticalSecondCallAfterRunning_IsIdempotent()
    {
        var (_, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-idempotent-runner-{Guid.NewGuid():N}");
        var job = JobGrain($"agent-job-idempotent-{Guid.NewGuid():N}");
        var input = MakeInput("same", projectId, "/tmp/agent-job-idempotent");

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        await job.SubmitAsync(input with { });

        Assert.Equal(AgentJobStatus.Running, await job.GetStatusAsync());
    }

}
