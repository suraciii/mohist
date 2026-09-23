using Mohist.Server.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Agent.Grain;

public partial class AgentJobGrainSpecs
{
    [Fact]
    public async Task InitialInputRecovery_ConvergesSessionAndJobBeforeStartAndAcceptsOnlyNewBindingReport()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-initial-recovery-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-initial-recovery-{Guid.NewGuid():N}";
        var sessionId = $"session-initial-recovery-{Guid.NewGuid():N}";
        var inputId = $"input-initial-recovery-{Guid.NewGuid():N}";
        var turnId = $"turn-initial-recovery-{Guid.NewGuid():N}";
        var oldRuntimeSessionId = $"runtime-old-{Guid.NewGuid():N}";
        var newRuntimeSessionId = $"runtime-new-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            "/tmp/initial-recovery",
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "agent-launch")
                .WithLabel("mohist.io/source-id", jobKey)
                .WithLabel("mohist.io/agent-id", "agent-test")));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId, turnId, "recover me", "agent-connection", jobKey, Runtime: "opencode", WorkDir: "/tmp/initial-recovery",
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "agent-launch")
                .WithLabel("mohist.io/source-id", jobKey)
                .WithLabel("mohist.io/agent-id", "agent-test")));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            oldRuntimeSessionId, WorkDir: "/tmp/initial-recovery", Runtime: "opencode"));
        await job.SubmitAsync(MakeInput("recover me", projectId, "/tmp/initial-recovery") with
        {
            AgentSessionId = sessionId,
            InitialInputId = inputId,
            InitialTurnId = turnId,
            Runtime = "opencode",
            Model = "frozen-model",
        });
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await job.RecordRuntimeSessionBindingAsync(runnerId, workId, sessionId, oldRuntimeSessionId));
        var recovery = new PrepareAgentJobInitialRecovery(
            "operation-1", runnerId, workId, TestRunnerGenerationExtensions.ProcessGeneration,
            sessionId, inputId, turnId, "opencode", oldRuntimeSessionId, "creation-attempt-1",
            AgentJobInitialRecoveryReasons.ConfiguredFallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.PrepareInitialInputRecoveryAsync(
            recovery with { ProcessGeneration = "stale-process" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.PrepareInitialInputRecoveryAsync(
            recovery with { RecoveryReason = null! }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.PrepareInitialInputRecoveryAsync(
            recovery with { RecoveryReason = "runtime-unavailable" }));
        var prepared = await job.PrepareInitialInputRecoveryAsync(recovery);
        Assert.True(prepared.CandidateCreationAuthorized);
        Assert.True((await job.PrepareInitialInputRecoveryAsync(recovery)).CandidateCreationAuthorized);
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.PrepareInitialInputRecoveryAsync(
            recovery with { CreationAttemptId = "duplicate-executor" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.PrepareInitialInputRecoveryAsync(
            recovery with { RecoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing }));

        var staleReport = await job.ReportResultAsync(runnerId, workId, new WorkResult(
            "completed", "stale", AgentSessionId: sessionId, AgentTurnId: turnId,
            Runtime: "opencode", RuntimeSessionId: oldRuntimeSessionId));
        Assert.Equal(WorkReportVerdict.Refused, staleReport.Verdict);

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.CompleteInitialInputRecoveryAsync(
            new CompleteAgentJobInitialRecovery(recovery, "codex", newRuntimeSessionId)));
        var completed = await job.CompleteInitialInputRecoveryAsync(new CompleteAgentJobInitialRecovery(
            recovery, "pi", newRuntimeSessionId));
        Assert.Equal("ready", completed.Phase);
        await AssertBindingRequiredForAllStatusesAsync(job, runnerId, workId);
        await AssertOldBindingRefusedForAllStatusesAsync(
            job, runnerId, workId, sessionId, turnId, "opencode", oldRuntimeSessionId);

        var started = await job.StartInitialInputAsync(new StartAgentJobInitialInput(
            "operation-1", "attempt-1", runnerId, workId,
            TestRunnerGenerationExtensions.ProcessGeneration, sessionId, inputId, turnId,
            "pi", newRuntimeSessionId));
        Assert.True(started.SubmissionAuthorized);
        Assert.True((await job.StartInitialInputAsync(new StartAgentJobInitialInput(
            "operation-1", "attempt-1", runnerId, workId,
            TestRunnerGenerationExtensions.ProcessGeneration, sessionId, inputId, turnId,
            "pi", newRuntimeSessionId))).SubmissionAuthorized);
        Assert.False((await job.StartInitialInputAsync(new StartAgentJobInitialInput(
            "operation-1", "duplicate-executor", runnerId, workId,
            TestRunnerGenerationExtensions.ProcessGeneration, sessionId, inputId, turnId,
            "pi", newRuntimeSessionId))).SubmissionAuthorized);
        await AssertBindingRequiredForAllStatusesAsync(job, runnerId, workId);
        await AssertOldBindingRefusedForAllStatusesAsync(
            job, runnerId, workId, sessionId, turnId, "opencode", oldRuntimeSessionId);

        var sessionAfter = await session.GetAsync();
        Assert.Equal(newRuntimeSessionId, sessionAfter!.AgentSessionId);
        Assert.Equal(2, sessionAfter.ContextGeneration);
        Assert.Equal("pi", sessionAfter.Runtime);
        var accepted = await job.ReportResultAsync(runnerId, workId, new WorkResult(
            "completed", "recovered", AgentSessionId: sessionId, AgentTurnId: turnId,
            Runtime: "pi", RuntimeSessionId: newRuntimeSessionId));
        Assert.Equal(WorkReportVerdict.Accepted, accepted.Verdict);
        Assert.Equal("frozen-model", (await job.GetTerminalResultAsync()).Model);
        await AssertBindingRequiredForAllStatusesAsync(job, runnerId, workId);
        await AssertOldBindingRefusedForAllStatusesAsync(
            job, runnerId, workId, sessionId, turnId, "opencode", oldRuntimeSessionId);
    }

    [Fact]
    public async Task InitialInputRecovery_RejectsSameRuntimeChangeAndManagerFallbackAtJobBoundary()
    {
        var sameRuntime = await CreateInitialRecoveryClaimAsync(manager: false);
        var sameRuntimeCommand = sameRuntime.Recovery with
        {
            RecoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing,
        };
        await sameRuntime.Job.PrepareInitialInputRecoveryAsync(sameRuntimeCommand);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sameRuntime.Job.CompleteInitialInputRecoveryAsync(
            new CompleteAgentJobInitialRecovery(sameRuntimeCommand, "pi", $"runtime-pi-{Guid.NewGuid():N}")));
        var sameRuntimeReceipt = await sameRuntime.Job.CompleteInitialInputRecoveryAsync(
            new CompleteAgentJobInitialRecovery(sameRuntimeCommand, "opencode", $"runtime-opencode-{Guid.NewGuid():N}"));
        Assert.Equal("opencode", sameRuntimeReceipt.Runtime);

        var manager = await CreateInitialRecoveryClaimAsync(manager: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Job.PrepareInitialInputRecoveryAsync(
            manager.Recovery with { RecoveryReason = AgentJobInitialRecoveryReasons.ConfiguredFallback }));
    }

    [Fact]
    public async Task InitialInputRecovery_LostSessionReplacementResponseConvergesAfterJobReload()
    {
        var claim = await CreateInitialRecoveryClaimAsync(manager: false);
        var recovery = claim.Recovery with { RecoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing };
        var replacementId = $"runtime-replacement-{Guid.NewGuid():N}";
        await claim.Job.PrepareInitialInputRecoveryAsync(recovery);
        var session = Grains.GetGrain<IAgentSessionGrain>(recovery.SessionId);
        var before = await session.GetAsync();

        var committed = await session.RecoverInitialAgentJobRuntimeSessionAsync(
            new RecoverInitialAgentJobRuntimeSessionCommand(
                recovery.OperationId,
                claim.Job.GetPrimaryKeyString(),
                recovery.WorkId,
                recovery.ProcessGeneration,
                recovery.RunnerId,
                recovery.InputId,
                recovery.TurnId,
                recovery.ExpectedRuntime,
                recovery.ExpectedRuntimeSessionId,
                "opencode",
                replacementId,
                before!.BindingEpoch));
        Assert.Equal(replacementId, committed.RuntimeSessionId);

        await TestLifecycle.DeactivateAndWait(claim.Job, Grains);
        var reloaded = JobGrain(claim.Job.GetPrimaryKeyString());
        var receipt = await reloaded.CompleteInitialInputRecoveryAsync(
            new CompleteAgentJobInitialRecovery(recovery, "opencode", replacementId));
        var replay = await reloaded.CompleteInitialInputRecoveryAsync(
            new CompleteAgentJobInitialRecovery(recovery, "opencode", replacementId));
        var after = await session.GetAsync();

        Assert.Equal("ready", receipt.Phase);
        Assert.Equal(replacementId, replay.RuntimeSessionId);
        Assert.Equal(before.ContextGeneration + 1, after!.ContextGeneration);
        Assert.Equal(before.BindingEpoch + 1, after.BindingEpoch);
    }

    [Fact]
    public async Task InitialInputRecovery_JobPersistFailureAfterSessionCommitReloadsBeforeSameActivationRetry()
    {
        var claim = await CreateInitialRecoveryClaimAsync(manager: false);
        var recovery = claim.Recovery with { RecoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing };
        var replacementId = $"runtime-replacement-{Guid.NewGuid():N}";
        await claim.Job.PrepareInitialInputRecoveryAsync(recovery);
        var failures = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<ReportPersistenceFailureProbe>();
        failures.FailNextAgentJobInitialInputPersist(claim.Job.GetPrimaryKeyString(), "ready");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            claim.Job.CompleteInitialInputRecoveryAsync(
                new CompleteAgentJobInitialRecovery(recovery, "opencode", replacementId)));

        var receipt = await claim.Job.CompleteInitialInputRecoveryAsync(
            new CompleteAgentJobInitialRecovery(recovery, "opencode", replacementId));
        var session = await Grains.GetGrain<IAgentSessionGrain>(recovery.SessionId).GetAsync();
        Assert.Equal("ready", receipt.Phase);
        Assert.Equal(replacementId, receipt.RuntimeSessionId);
        Assert.Equal(2, session!.ContextGeneration);
    }

    [Fact]
    public async Task InitialInputStart_JobPersistFailureReloadsAndOnlySameLiveAttemptCanRecoverAuthority()
    {
        var claim = await CreateInitialRecoveryClaimAsync(manager: false);
        var recovery = claim.Recovery with { RecoveryReason = AgentJobInitialRecoveryReasons.SameRuntimeMissing };
        var replacementId = $"runtime-replacement-{Guid.NewGuid():N}";
        await claim.Job.PrepareInitialInputRecoveryAsync(recovery);
        await claim.Job.CompleteInitialInputRecoveryAsync(
            new CompleteAgentJobInitialRecovery(recovery, "opencode", replacementId));
        var start = new StartAgentJobInitialInput(
            recovery.OperationId,
            "live-invocation-1",
            recovery.RunnerId,
            recovery.WorkId,
            recovery.ProcessGeneration,
            recovery.SessionId,
            recovery.InputId,
            recovery.TurnId,
            "opencode",
            replacementId);
        var failures = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<ReportPersistenceFailureProbe>();
        failures.FailNextAgentJobInitialInputPersist(claim.Job.GetPrimaryKeyString(), "started");

        await Assert.ThrowsAsync<InvalidOperationException>(() => claim.Job.StartInitialInputAsync(start));
        await TestLifecycle.DeactivateAndWait(claim.Job, Grains);
        var reloaded = JobGrain(claim.Job.GetPrimaryKeyString());
        Assert.False((await reloaded.StartInitialInputAsync(
            start with { SubmissionAttemptId = "distinct-invocation" })).SubmissionAuthorized);
        Assert.True((await reloaded.StartInitialInputAsync(start)).SubmissionAuthorized);

        var session = Grains.GetGrain<IAgentSessionGrain>(recovery.SessionId);
        await session.MarkInitialTurnTerminalAsync(
            claim.Job.GetPrimaryKeyString(), AgentTurnStatus.Failed, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reloaded.StartInitialInputAsync(start));
    }

    private async Task<(IAgentJobGrain Job, PrepareAgentJobInitialRecovery Recovery)> CreateInitialRecoveryClaimAsync(
        bool manager)
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-initial-recovery-guard-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-initial-recovery-guard-{Guid.NewGuid():N}";
        var sessionId = $"session-initial-recovery-guard-{Guid.NewGuid():N}";
        var inputId = $"input-initial-recovery-guard-{Guid.NewGuid():N}";
        var turnId = $"turn-initial-recovery-guard-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-initial-recovery-guard-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            "/tmp/initial-recovery-guard",
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "agent-launch")
                .WithLabel("mohist.io/source-id", jobKey)
                .WithLabel("mohist.io/agent-id", "agent-test")));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            "recover me",
            "agent-connection",
            jobKey,
            Runtime: "opencode",
            WorkDir: "/tmp/initial-recovery-guard",
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "agent-launch")
                .WithLabel("mohist.io/source-id", jobKey)
                .WithLabel("mohist.io/agent-id", "agent-test")));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            runtimeSessionId,
            WorkDir: "/tmp/initial-recovery-guard",
            Runtime: "opencode"));

        var input = MakeInput("recover me", projectId, "/tmp/initial-recovery-guard") with
        {
            AgentSessionId = sessionId,
            InitialInputId = inputId,
            InitialTurnId = turnId,
            Runtime = "opencode",
            PinnedRunnerId = runnerId,
        };
        if (manager)
        {
            input = input with
            {
                ExecutionSource = AgentExecutionSources.Slack,
                SlackExecutionContext = SlackExecutionContextFactory.Create(
                    "workspace-1",
                    "conversation-1",
                    "message-1",
                    "message-1",
                    "member-1",
                    "connection-1",
                    sessionId,
                    $"slack:{sessionId}:{inputId}",
                    projectId: SlackDeliveryOwnerIds.ManagerProjectId,
                    ownerKind: SlackDeliveryOwnerKinds.Manager),
            };
        }

        await job.SubmitAsync(input);
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await job.RecordRuntimeSessionBindingAsync(runnerId, workId, sessionId, runtimeSessionId));
        return (job, new PrepareAgentJobInitialRecovery(
            $"operation-{Guid.NewGuid():N}",
            runnerId,
            workId,
            TestRunnerGenerationExtensions.ProcessGeneration,
            sessionId,
            inputId,
            turnId,
            "opencode",
            runtimeSessionId,
            $"creation-attempt-{Guid.NewGuid():N}",
            AgentJobInitialRecoveryReasons.SameRuntimeMissing));
    }

    private static async Task AssertBindingRequiredForAllStatusesAsync(
        IAgentJobGrain job,
        string runnerId,
        string workId)
    {
        foreach (var status in new[] { "completed", "failed", "timeout", "unknown" })
        {
            var report = await job.ReportResultAsync(runnerId, workId, new WorkResult(status, "late binding-less report"));
            Assert.Equal(WorkReportVerdict.Refused, report.Verdict);
            Assert.Equal("execution-binding-required", report.Reason);
        }
    }

    [Fact]
    public async Task InitialInputRecovery_SecondJobContinuation_RecoversMissingRuntimeBeforeProvider()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync($"agent-job-second-recovery-{Guid.NewGuid():N}");
        var sessionId = $"session-second-recovery-{Guid.NewGuid():N}";
        var oldRuntimeSessionId = $"runtime-old-{Guid.NewGuid():N}";
        var newRuntimeSessionId = $"runtime-new-{Guid.NewGuid():N}";
        var workDir = "/tmp/second-recovery";
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", projectId)
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/source-id", sessionId)
            .WithLabel("mohist.io/agent-id", "agent-test");
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(runnerId, "opencode", workDir, Metadata: metadata));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            oldRuntimeSessionId, WorkDir: workDir, Runtime: "opencode"));

        // First Job on the logical Session: claim, bind, real provider
        // admission, then terminal.
        var job1Key = $"agent-job-second-recovery-1-{Guid.NewGuid():N}";
        var input1 = $"input-second-recovery-1-{Guid.NewGuid():N}";
        var turn1 = $"turn-second-recovery-1-{Guid.NewGuid():N}";
        var job1 = JobGrain(job1Key);
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            input1, turn1, "first prompt", "agent-connection", job1Key,
            Runtime: "opencode", WorkDir: workDir, Metadata: metadata));
        await job1.SubmitAsync(MakeInput("first prompt", projectId, workDir) with
        {
            AgentSessionId = sessionId,
            InitialInputId = input1,
            InitialTurnId = turn1,
            Runtime = "opencode",
        });
        await WaitForStatusAsync(job1, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var work1 = (await job1.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await job1.RecordRuntimeSessionBindingAsync(runnerId, work1, sessionId, oldRuntimeSessionId));
        Assert.True((await job1.StartInitialInputAsync(new StartAgentJobInitialInput(
            $"operation-{job1Key}", "attempt-1", runnerId, work1, TestRunnerGenerationExtensions.ProcessGeneration,
            sessionId, input1, turn1, "opencode", oldRuntimeSessionId))).SubmissionAuthorized);
        Assert.Equal(WorkReportVerdict.Accepted, (await job1.ReportResultAsync(
            runnerId, work1, new WorkResult(
                "completed", "first done", AgentSessionId: sessionId, AgentTurnId: turn1,
                Runtime: "opencode", RuntimeSessionId: oldRuntimeSessionId))).Verdict);

        // Second Job on the same logical Session appends its own Job-owned
        // Input and Turn, then its physical runtime session goes missing
        // before the provider ever saw the Input.
        var job2Key = $"agent-job-second-recovery-2-{Guid.NewGuid():N}";
        var input2 = $"input-second-recovery-2-{Guid.NewGuid():N}";
        var turn2 = $"turn-second-recovery-2-{Guid.NewGuid():N}";
        var job2 = JobGrain(job2Key);
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            input2, turn2, "second prompt", "agent-connection", job2Key,
            Runtime: "opencode", WorkDir: workDir, Metadata: metadata));
        await job2.SubmitAsync(MakeInput("second prompt", projectId, workDir) with
        {
            AgentSessionId = sessionId,
            InitialInputId = input2,
            InitialTurnId = turn2,
            Runtime = "opencode",
        });
        await WaitForStatusAsync(job2, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var work2 = (await job2.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await job2.RecordRuntimeSessionBindingAsync(runnerId, work2, sessionId, oldRuntimeSessionId));

        var recovery = new PrepareAgentJobInitialRecovery(
            $"operation-{job2Key}", runnerId, work2, TestRunnerGenerationExtensions.ProcessGeneration,
            sessionId, input2, turn2, "opencode", oldRuntimeSessionId, "creation-attempt-1",
            AgentJobInitialRecoveryReasons.SameRuntimeMissing);
        Assert.True((await job2.PrepareInitialInputRecoveryAsync(recovery)).CandidateCreationAuthorized);
        var completedRecovery = await job2.CompleteInitialInputRecoveryAsync(new CompleteAgentJobInitialRecovery(
            recovery, "opencode", newRuntimeSessionId));
        Assert.Equal("ready", completedRecovery.Phase);
        var sessionAfterRecovery = await session.GetAsync();
        Assert.Equal(newRuntimeSessionId, sessionAfterRecovery!.AgentSessionId);

        var started = await job2.StartInitialInputAsync(new StartAgentJobInitialInput(
            $"operation-{job2Key}", "attempt-1", runnerId, work2, TestRunnerGenerationExtensions.ProcessGeneration,
            sessionId, input2, turn2, "opencode", newRuntimeSessionId));
        Assert.True(started.SubmissionAuthorized);
        Assert.True((await job2.StartInitialInputAsync(new StartAgentJobInitialInput(
            $"operation-{job2Key}", "attempt-1", runnerId, work2, TestRunnerGenerationExtensions.ProcessGeneration,
            sessionId, input2, turn2, "opencode", newRuntimeSessionId))).SubmissionAuthorized);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Sessions.IAgentSessionStore>()
                .LoadAsync(sessionId);
            Assert.NotNull(stored);
            var receipt = stored!.Status.InitialInputOperation!;
            Assert.Equal(job2Key, receipt.JobId);
            Assert.True(receipt.EffectAdmitted);
            Assert.Equal(newRuntimeSessionId, receipt.RuntimeSessionId);
            Assert.Equal(job1Key, stored.Status.Turns!.Single(turn => turn.Id == turn1).JobId);
            Assert.Equal(AgentTurnStatus.Completed, stored.Status.Turns!.Single(turn => turn.Id == turn1).Status);
            var storedTurn2 = stored.Status.Turns!.Single(turn => turn.Id == turn2);
            Assert.Equal(job2Key, storedTurn2.JobId);
            Assert.Equal(AgentTurnStatus.Executing, storedTurn2.Status);
            Assert.Equal(job2Key, stored.Status.Inputs!.Single(input => input.Id == input2).JobId);
        }

        Assert.Equal(WorkReportVerdict.Accepted, (await job2.ReportResultAsync(
            runnerId, work2, new WorkResult(
                "completed", "second done", AgentSessionId: sessionId, AgentTurnId: turn2,
                Runtime: "opencode", RuntimeSessionId: newRuntimeSessionId))).Verdict);
    }

    private static async Task AssertOldBindingRefusedForAllStatusesAsync(
        IAgentJobGrain job,
        string runnerId,
        string workId,
        string sessionId,
        string turnId,
        string runtime,
        string runtimeSessionId)
    {
        foreach (var status in new[] { "completed", "failed", "timeout", "unknown" })
        {
            var report = await job.ReportResultAsync(runnerId, workId, new WorkResult(
                status,
                "late old-target report",
                AgentSessionId: sessionId,
                AgentTurnId: turnId,
                Runtime: runtime,
                RuntimeSessionId: runtimeSessionId));
            Assert.Equal(WorkReportVerdict.Refused, report.Verdict);
        }
    }

}
