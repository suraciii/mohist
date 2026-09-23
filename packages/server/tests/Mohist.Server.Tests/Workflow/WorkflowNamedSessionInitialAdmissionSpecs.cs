using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Workflow;

// Desired-behavior regression for the Job-owned named Workflow continuation:
// one logical Session per name, one Job-owned Input/Turn per invocation, and a
// real initial-provider admission boundary that hands its receipt from one
// definitely-terminal Job Turn to the next Job exactly once.
[Collection("WorkflowExecution")]
[Trait("level", "L1")]
public sealed class WorkflowNamedSessionInitialAdmissionSpecs : WorkflowGrainSpecs
{
    public WorkflowNamedSessionInitialAdmissionSpecs(WorkflowGrainFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task NamedReuseSecondJob_AdmitsJobOwnedTurnOnceAndCompletesWorkflow()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [
                AgentTask("first", "First", "delivery"),
                AgentTask("second", "Second", "delivery"),
            ], [])
        ]);
        var workflow = await StartWorkflowAsync(definition, $"workflow-named-admission-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);

        // First invocation: the real Runner flow claims the Job, opens the
        // pre-created Session with its Runner identity, attaches the physical
        // runtime session, and asks the Job owner for initial provider
        // admission. Nothing reports a result first: admission is the first
        // execution effect on the Session.
        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var first = await PollWorkAsync(runnerId);
        var runtime = first.Work.AgentDefinition?.Runtime
            ?? throw new InvalidOperationException("The AgentJob dispatch carries no runtime.");
        var runtimeSessionId = $"runtime-{first.Work.WorkId}";
        var sessionGrain = Grains.GetGrain<IAgentSessionGrain>(first.Work.AgentSessionId!);
        await sessionGrain.OpenAsync(new OpenAgentSessionCommand(runnerId, runtime));
        await sessionGrain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId, Runtime: runtime));
        var job1 = Grains.GetGrain<IAgentJobGrain>(first.Work.AgentJobId!);
        Assert.True(await job1.RecordRuntimeSessionBindingAsync(
            runnerId, first.Work.WorkId, first.Work.AgentSessionId!, runtimeSessionId));

        var start1 = new StartAgentJobInitialInput(
            $"initial-start-{first.Work.AgentJobId}",
            $"submission-{first.Work.AgentJobId}-1",
            runnerId,
            first.Work.WorkId,
            TestRunnerGenerationExtensions.ProcessGeneration,
            first.Work.AgentSessionId!,
            first.Work.InitialInputId!,
            first.Work.InitialTurnId!,
            runtime,
            runtimeSessionId);
        var receipt1 = await job1.StartInitialInputAsync(start1);
        Assert.True(receipt1.SubmissionAuthorized);

        var firstAdmission = await LoadSessionAsync(first.Work.AgentSessionId!);
        var firstOperation = firstAdmission!.Status.InitialInputOperation!;
        Assert.Equal(first.Work.AgentJobId, firstOperation.JobId);
        Assert.Equal(first.Work.InitialInputId, firstOperation.InputId);
        Assert.Equal(first.Work.InitialTurnId, firstOperation.TurnId);
        Assert.True(firstOperation.EffectAdmitted);
        Assert.Equal(AgentTurnStatus.Executing,
            firstAdmission.Status.Turns!.Single(turn => turn.Id == first.Work.InitialTurnId).Status);

        // The ReportAsync helper bypasses admission; using it here is valid
        // only because the first Job already passed the real admission
        // boundary above. It terminalizes the first Job and lets the Workflow
        // render the second same-named invocation.
        await ReportAsync(runnerId, first.Work, "completed");
        Assert.Equal(AgentJobStatus.Completed, (await job1.GetRuntimeSnapshotAsync()).Status);

        // Second invocation: the same logical Session, a distinct Job-owned
        // Input and Turn minted from the Stage-scoped invocation identity.
        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var second = await PollWorkAsync(runnerId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, second.Work.OwnerKind);
        Assert.Equal(first.Work.AgentSessionId, second.Work.AgentSessionId);
        Assert.NotEqual(first.Work.AgentJobId, second.Work.AgentJobId);
        Assert.NotEqual(first.Work.InitialInputId, second.Work.InitialInputId);
        Assert.NotEqual(first.Work.InitialTurnId, second.Work.InitialTurnId);

        var storedSecond = await LoadSessionAsync(second.Work.AgentSessionId!);
        var secondInput = storedSecond!.Status.Inputs!.Single(input =>
            string.Equals(input.Id, second.Work.InitialInputId, StringComparison.Ordinal));
        var secondTurn = storedSecond.Status.Turns!.Single(turn =>
            string.Equals(turn.Id, second.Work.InitialTurnId, StringComparison.Ordinal));
        Assert.Equal(second.Work.AgentJobId, secondInput.JobId);
        Assert.Equal(second.Work.AgentJobId, secondTurn.JobId);
        Assert.Equal(AgentTurnStatus.Queued, secondTurn.Status);
        Assert.Equal(AgentTurnStatus.Completed, storedSecond.Status.Turns!.Single(turn =>
            string.Equals(turn.Id, first.Work.InitialTurnId, StringComparison.Ordinal)).Status);

        // The Runner keeps the same physical runtime session for the reused
        // logical Session, exactly as the named-reuse flow intends, and asks
        // the second Job owner for admission through the same real boundary.
        await sessionGrain.OpenAsync(new OpenAgentSessionCommand(runnerId, runtime));
        await sessionGrain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId, Runtime: runtime));
        var job2 = Grains.GetGrain<IAgentJobGrain>(second.Work.AgentJobId!);
        Assert.True(await job2.RecordRuntimeSessionBindingAsync(
            runnerId, second.Work.WorkId, second.Work.AgentSessionId!, runtimeSessionId));
        var start2 = new StartAgentJobInitialInput(
            $"initial-start-{second.Work.AgentJobId}",
            $"submission-{second.Work.AgentJobId}-1",
            runnerId,
            second.Work.WorkId,
            TestRunnerGenerationExtensions.ProcessGeneration,
            second.Work.AgentSessionId!,
            second.Work.InitialInputId!,
            second.Work.InitialTurnId!,
            runtime,
            runtimeSessionId);
        var receipt2 = await job2.StartInitialInputAsync(start2);
        Assert.True(receipt2.SubmissionAuthorized);

        var secondAdmission = await LoadSessionAsync(second.Work.AgentSessionId!);
        var secondOperation = secondAdmission!.Status.InitialInputOperation!;
        Assert.Equal(second.Work.AgentJobId, secondOperation.JobId);
        Assert.Equal(second.Work.InitialInputId, secondOperation.InputId);
        Assert.Equal(second.Work.InitialTurnId, secondOperation.TurnId);
        Assert.True(secondOperation.EffectAdmitted);
        Assert.NotNull(secondOperation.EffectAdmittedAt);
        var turn2 = secondAdmission.Status.Turns!.Single(turn =>
            string.Equals(turn.Id, second.Work.InitialTurnId, StringComparison.Ordinal));
        Assert.Equal(AgentTurnStatus.Executing, turn2.Status);
        Assert.Equal(second.Work.AgentJobId, turn2.JobId);

        // An exact retry of the same second-Job operation preserves the
        // admitted receipt byte for byte instead of re-authorizing an effect.
        var retry2 = await job2.StartInitialInputAsync(start2);
        Assert.True(retry2.SubmissionAuthorized);
        var afterRetry = await LoadSessionAsync(second.Work.AgentSessionId!);
        Assert.Equal(secondOperation, afterRetry!.Status.InitialInputOperation);

        // A late first-Job start cannot regain provider authorization after
        // the second receipt: the Job owner fence refuses the terminal Job
        // before the Session boundary is even reached.
        var staleStart = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job1.StartInitialInputAsync(start1));
        Assert.Equal("initial_input_start_claim_mismatch", staleStart.Message);
        var afterStaleStart = await LoadSessionAsync(second.Work.AgentSessionId!);
        Assert.Equal(secondOperation, afterStaleStart!.Status.InitialInputOperation);

        // A late first-Job result is arbitrated away from the terminal Job and
        // cannot overwrite the second Job's executing Turn or receipt.
        var staleReport = await job1.ReportResultAsync(
            runnerId,
            first.Work.WorkId,
            new WorkResult("failed", "late first-Job failure"));
        Assert.Equal(WorkReportVerdict.Refused, staleReport.Verdict);
        var afterStaleReport = await LoadSessionAsync(second.Work.AgentSessionId!);
        Assert.Equal(AgentTurnStatus.Executing, afterStaleReport!.Status.Turns!.Single(turn =>
            string.Equals(turn.Id, second.Work.InitialTurnId, StringComparison.Ordinal)).Status);
        Assert.Equal(secondOperation, afterStaleReport.Status.InitialInputOperation);

        // One second-Job terminal result settles its own Turn and completes
        // the Workflow; the first Job's terminal facts stay untouched.
        var completed = await job2.ReportResultAsync(
            runnerId,
            second.Work.WorkId,
            new WorkResult(
                "completed",
                AgentSessionId: second.Work.AgentSessionId,
                AgentTurnId: second.Work.InitialTurnId,
                Runtime: runtime,
                RuntimeSessionId: runtimeSessionId));
        Assert.True(completed.Accepted, completed.Reason);
        await Services.GetRequiredService<IEventDispatcher>().DrainAsync();

        Assert.Equal(AgentJobStatus.Completed, (await job2.GetRuntimeSnapshotAsync()).Status);
        Assert.Equal(AgentJobStatus.Completed, (await job1.GetRuntimeSnapshotAsync()).Status);
        var final = await LoadSessionAsync(second.Work.AgentSessionId!);
        Assert.Equal(AgentTurnStatus.Completed, final!.Status.Turns!.Single(turn =>
            string.Equals(turn.Id, second.Work.InitialTurnId, StringComparison.Ordinal)).Status);
        Assert.Equal(AgentTurnStatus.Completed, final.Status.Turns!.Single(turn =>
            string.Equals(turn.Id, first.Work.InitialTurnId, StringComparison.Ordinal)).Status);
        Assert.Equal(secondOperation.JobId, final.Status.InitialInputOperation!.JobId);
        Assert.Equal(WorkflowRunStatus.Completed, (await LoadRunAsync(_workflowId!)).Status);
    }

    [Fact]
    public void SecondJobReceipt_ReplacesOnlyDefinitelyTerminalPriorReceipt()
    {
        var session = NamedReuseSession(
            out var firstOperation,
            out var secondOperation,
            priorTurnStatus: AgentTurnStatus.Completed);

        session.AdmitInitialAgentJobInputEffect(secondOperation, TestTime.UtcDateTime.AddMinutes(4));

        var receipt = session.Status.InitialInputOperation!;
        Assert.Equal(secondOperation.OperationId, receipt.OperationId);
        Assert.Equal(secondOperation.JobId, receipt.JobId);
        Assert.True(receipt.EffectAdmitted);
        Assert.Equal(TestTime.UtcDateTime.AddMinutes(4), receipt.RecordedAt);
        Assert.Equal(AgentTurnStatus.Completed, session.Status.Turns!.Single(turn => turn.Id == "turn-1").Status);
        var secondTurn = session.Status.Turns!.Single(turn => turn.Id == "turn-2");
        Assert.Equal(AgentTurnStatus.Executing, secondTurn.Status);
        Assert.Equal("job-2", secondTurn.JobId);
        Assert.NotEqual(firstOperation.OperationId, receipt.OperationId);
    }

    [Fact]
    public void LateFirstJobOperation_CannotRegainAuthorizationAfterSecondReceipt()
    {
        var session = NamedReuseSession(
            out var firstOperation,
            out var secondOperation,
            priorTurnStatus: AgentTurnStatus.Completed);
        session.AdmitInitialAgentJobInputEffect(secondOperation, TestTime.UtcDateTime.AddMinutes(4));

        // While the second Job's Turn is still executing, the first Job's
        // operation is a conflict against the current receipt.
        Assert.Equal("initial_input_start_conflict", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(firstOperation, TestTime.UtcDateTime.AddMinutes(5))).Message);

        // Once the second Turn is terminal, the old operation still cannot be
        // re-admitted: its own Turn is settled, not queued.
        session.MarkTurnTerminal("turn-2", AgentTurnStatus.Completed, null, TestTime.UtcDateTime.AddMinutes(6));
        Assert.Equal("initial_input_start_fence_mismatch", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(firstOperation, TestTime.UtcDateTime.AddMinutes(7))).Message);

        var receipt = session.Status.InitialInputOperation!;
        Assert.Equal(secondOperation.OperationId, receipt.OperationId);
        Assert.Equal("job-2", receipt.JobId);
    }

    [Theory]
    [InlineData(AgentTurnStatus.Executing)]
    [InlineData(AgentTurnStatus.Unknown)]
    public void UnresolvedPriorReceipt_BlocksSecondJobAdmission(AgentTurnStatus priorTurnStatus)
    {
        var session = NamedReuseSession(
            out _,
            out var secondOperation,
            priorTurnStatus);

        Assert.Equal("initial_input_start_conflict", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(secondOperation, TestTime.UtcDateTime.AddMinutes(4))).Message);
        Assert.Equal("job-1", session.Status.InitialInputOperation!.JobId);
        Assert.Equal(AgentTurnStatus.Queued, session.Status.Turns!.Single(turn => turn.Id == "turn-2").Status);
    }

    [Fact]
    public void JobOwnedHeadAdmits_WhileOnlyALaterOrdinaryQueuedTurnWaits()
    {
        var session = CreateDomainSession();
        session.AttachPhysicalSession("runtime-first", null, "/work", null, null, TestTime.UtcDateTime);
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "first prompt", "workflow", "job-1", TestTime.UtcDateTime);
        session.AcceptFollowup(
            "later-input",
            "later-turn",
            "operation-later",
            "later prompt",
            "api",
            "later-key",
            TestTime.UtcDateTime.AddMinutes(1),
            forceNewTurn: true);
        var operation = DomainOperation(session, "operation-1", "job-1", "work-1", "input-1", "turn-1", "runtime-first");

        session.AdmitInitialAgentJobInputEffect(operation, TestTime.UtcDateTime.AddMinutes(2));

        Assert.True(session.Status.InitialInputOperation!.EffectAdmitted);
        Assert.Equal(AgentTurnStatus.Executing,
            session.Status.Turns!.Single(turn => turn.Id == "turn-1").Status);
        Assert.Equal(AgentTurnStatus.Queued,
            session.Status.Turns!.Single(turn => turn.Id == "later-turn").Status);
    }

    [Fact]
    public void EarlierOrdinaryQueuedTurn_BlocksJobAdmissionFailClosed()
    {
        var session = CreateDomainSession();
        session.AttachPhysicalSession("runtime-first", null, "/work", null, null, TestTime.UtcDateTime);
        session.AcceptFollowup(
            "earlier-input",
            "earlier-turn",
            "operation-earlier",
            "earlier prompt",
            "api",
            "earlier-key",
            TestTime.UtcDateTime,
            forceNewTurn: true);
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "job prompt", "workflow", "job-1", TestTime.UtcDateTime.AddMinutes(1));
        var operation = DomainOperation(session, "operation-1", "job-1", "work-1", "input-1", "turn-1", "runtime-first");

        Assert.Equal("initial_input_start_fence_mismatch", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(operation, TestTime.UtcDateTime.AddMinutes(2))).Message);
        Assert.Equal(AgentTurnStatus.Queued,
            session.Status.Turns!.Single(turn => turn.Id == "turn-1").Status);
        Assert.Null(session.Status.InitialInputOperation);
    }

    [Theory]
    [InlineData(AgentTurnStatus.Executing)]
    [InlineData(AgentTurnStatus.Unknown)]
    public void CurrentExecutingOrUnknownTurn_BlocksJobAdmissionFailClosed(AgentTurnStatus fence)
    {
        var session = CreateDomainSession();
        session.AttachPhysicalSession("runtime-first", null, "/work", null, null, TestTime.UtcDateTime);
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "first prompt", "workflow", "job-1", TestTime.UtcDateTime);
        session.Status = session.Status with
        {
            Turns = session.Status.Turns!.Select(turn => turn.Id == "turn-1"
                ? turn with { Status = fence }
                : turn).ToArray(),
        };
        session.EnsureInitialLaunch(
            "input-2", "turn-2", "second prompt", "workflow", "job-2", TestTime.UtcDateTime.AddMinutes(1));
        var operation = DomainOperation(session, "operation-2", "job-2", "work-2", "input-2", "turn-2", "runtime-first");

        Assert.Equal("initial_input_start_fence_mismatch", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(operation, TestTime.UtcDateTime.AddMinutes(2))).Message);
        Assert.Equal(AgentTurnStatus.Queued,
            session.Status.Turns!.Single(turn => turn.Id == "turn-2").Status);
        Assert.Null(session.Status.InitialInputOperation);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("reset")]
    public void PendingStopOrReset_BlocksJobAdmissionFailClosed(string fence)
    {
        var session = CreateDomainSession();
        session.AttachPhysicalSession("runtime-first", null, "/work", null, null, TestTime.UtcDateTime);
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "first prompt", "workflow", "job-1", TestTime.UtcDateTime);
        session.Status = session.Status with
        {
            PendingStop = fence == "stop" ? new AgentSessionStopClaim("turn-1", "stop-operation") : null,
            PendingReset = fence == "reset"
                ? new AgentSessionResetReservation("reset-operation", null, "pi", TestTime.UtcDateTime)
                : null,
        };
        var operation = DomainOperation(session, "operation-1", "job-1", "work-1", "input-1", "turn-1", "runtime-first");

        Assert.Equal("initial_input_start_fence_mismatch", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(operation, TestTime.UtcDateTime.AddMinutes(1))).Message);
        Assert.Equal(AgentTurnStatus.Queued,
            session.Status.Turns!.Single(turn => turn.Id == "turn-1").Status);
        Assert.Null(session.Status.InitialInputOperation);
    }

    [Fact]
    public void RecordedButUnadmittedPriorReceipt_BlocksDifferentJobAdmission()
    {
        var session = CreateDomainSession();
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "first prompt", "workflow", "job-1", TestTime.UtcDateTime);
        session.AttachPhysicalSession("runtime-old", null, "/work", null, null, TestTime.UtcDateTime);
        // Pre-submission recovery records the first Job's receipt without
        // admitting an effect and retargets its queued Turn to a new binding.
        session.RecoverInitialAgentJobTurn(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            DomainOperation(session, "operation-1", "job-1", "work-1", "input-1", "turn-1", "runtime-new"),
            TestTime.UtcDateTime.AddMinutes(1),
            session.BindingEpoch);
        var recorded = session.Status.InitialInputOperation!;
        Assert.False(recorded.EffectAdmitted);

        var secondOperation = DomainOperation(session, "operation-2", "job-2", "work-2", "input-2", "turn-2", "runtime-new");

        Assert.Equal("initial_input_operation_conflict", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(secondOperation, TestTime.UtcDateTime.AddMinutes(2))).Message);
        Assert.Equal(recorded, session.Status.InitialInputOperation);
    }

    /// <summary>
    /// Builds the named-reuse shape at the domain boundary: a first Job launch
    /// admitted on a bound Session with its Turn left in
    /// <paramref name="priorTurnStatus"/>, and a second Job-owned Input and
    /// Turn appended to the same Session.
    /// </summary>
    [Fact]
    public async Task NamedReuseMissingSession_FailsDeterministicallyWithoutRecreatingTarget()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [AgentTask("build", "Build the change", "delivery")], [])
        ]);
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-reuse-missing-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);
        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var run = await LoadRunAsync(_workflowId!);
        var attempt = Assert.Single(run.CurrentStage().Tasks);
        var bootstrapHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(WorkflowAgentHandoffCodec.KeyFor(
            run.Metadata.ProjectId!,
            run.Id,
            run.CurrentStage().Id,
            attempt.Id,
            attempt.WorkId!));
        await bootstrapHandoff.ActivateAsync();
        var bootstrapPlan = await bootstrapHandoff.GetPlanAsync();
        Assert.NotNull(bootstrapPlan?.Invocation);
        var bootstrapWork = (await PollWorkAsync(runnerId)).Work;
        await ReportAsync(runnerId, bootstrapWork, "completed");

        // The persisted Session disappears (for example a store rollback)
        // and the cached grain state is evicted, so the next named reuse
        // finds no target.
        var sessionId = bootstrapPlan!.Invocation!.SessionId;
        var sessionGrain = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await TestLifecycle.DeactivateAndWait(sessionGrain, Grains);
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAgentSessionStore>().DeleteAsync(sessionId);
        }
        Assert.Null(await sessionGrain.GetAsync());

        const string reuseIdentity = "apply-feedback.1";
        var reuseCommand = bootstrapPlan.Command with
        {
            CommandId = reuseIdentity,
            ActionAttemptId = reuseIdentity,
            Prompt = "Apply the feedback",
            ReuseSessionId = sessionId,
            Completion = bootstrapPlan.Command.Completion! with { WorkId = reuseIdentity },
        };
        var reuseHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(reuseCommand));
        var prepared = await reuseHandoff.PrepareAsync(reuseCommand);
        await reuseHandoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            reuseIdentity,
            WorkflowAgentHandoffCodec.Fingerprint(reuseCommand)));
        var failed = await reuseHandoff.ActivateAsync();

        // No new Session is fabricated under the reuse identity, no Input,
        // Turn, or provider effect appears, and the prepared Job is not
        // abandoned Running: the deterministic dead end aborts it.
        Assert.Equal(WorkflowAgentHandoffDisposition.Failed, failed.Disposition);
        var failedPlan = await reuseHandoff.GetPlanAsync();
        Assert.Equal("agent_session_reuse_target_missing", failedPlan!.Rejection?.Code);
        Assert.Contains(sessionId, failedPlan.ActivationError, StringComparison.Ordinal);
        Assert.Equal(WorkflowAgentActivationStep.EnsureSession, failedPlan.ActivationStep);
        Assert.Equal(AgentJobStatus.Cancelled,
            await Grains.GetGrain<IAgentJobGrain>(prepared.Invocation!.JobKey).GetStatusAsync());
        Assert.Null(await LoadSessionAsync(sessionId));
        Assert.Null(await sessionGrain.GetAsync());

        // The failure is terminal and replays stably without reminder spin.
        await reuseHandoff.TriggerActivationAsync();
        var replay = await reuseHandoff.ActivateAsync();
        Assert.Equal(WorkflowAgentHandoffDisposition.Failed, replay.Disposition);
        var replayPlan = await reuseHandoff.GetPlanAsync();
        Assert.Equal("agent_session_reuse_target_missing", replayPlan!.Rejection?.Code);
        Assert.Equal(failedPlan.ActivationError, replayPlan.ActivationError);
        Assert.Null(await LoadSessionAsync(sessionId));
    }

    [Fact]
    public async Task LegacyJobIdNullContinuation_FailsOnceWithoutRelabelOrReminderSpin()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [AgentTask("build", "Build the change", "delivery")], [])
        ]);
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-legacy-null-job-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);
        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var run = await LoadRunAsync(_workflowId!);
        var attempt = Assert.Single(run.CurrentStage().Tasks);
        var bootstrapHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(WorkflowAgentHandoffCodec.KeyFor(
            run.Metadata.ProjectId!,
            run.Id,
            run.CurrentStage().Id,
            attempt.Id,
            attempt.WorkId!));
        await bootstrapHandoff.ActivateAsync();
        var bootstrapPlan = await bootstrapHandoff.GetPlanAsync();
        Assert.NotNull(bootstrapPlan?.Invocation);
        var bootstrapWork = (await PollWorkAsync(runnerId)).Work;
        await ReportAsync(runnerId, bootstrapWork, "completed");
        var sessionId = bootstrapPlan!.Invocation!.SessionId;
        await Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand($"runtime-{sessionId}"));

        // A handoff accepted before the Job-owned seam kept its follow-up as
        // a JobId-null Input and Turn under the exact pre-minted ids.
        const string reuseIdentity = "apply-feedback.1";
        var reuseCommand = bootstrapPlan.Command with
        {
            CommandId = reuseIdentity,
            ActionAttemptId = reuseIdentity,
            Prompt = "Apply the feedback",
            ReuseSessionId = sessionId,
            Completion = bootstrapPlan.Command.Completion! with { WorkId = reuseIdentity },
        };
        var invocation = WorkflowAgentHandoffCodec.InvocationFor(reuseCommand);
        var sessionGrain = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await sessionGrain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: reuseCommand.Prompt,
            Source: "workflow",
            IdempotencyKey: invocation.InvocationId,
            PreMintedInputId: invocation.InputId,
            PreMintedTurnId: invocation.TurnId,
            ForceNewTurn: true,
            ExpectedProjectId: reuseCommand.ProjectId,
            ExpectedAgentId: bootstrapPlan.AgentId));
        var seeded = await LoadSessionAsync(sessionId);
        var seededInput = seeded!.Status.Inputs!.Single(input => input.Id == invocation.InputId);
        var seededTurn = seeded.Status.Turns!.Single(turn => turn.Id == invocation.TurnId);
        Assert.Null(seededInput.JobId);
        Assert.Null(seededTurn.JobId);

        var reuseHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(reuseCommand));
        var prepared = await reuseHandoff.PrepareAsync(reuseCommand);
        await reuseHandoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            reuseIdentity,
            WorkflowAgentHandoffCodec.Fingerprint(reuseCommand)));
        var failed = await reuseHandoff.ActivateAsync();

        // The deterministic identity conflict settles once: the accepted
        // facts are never relabeled or replayed, the prepared Job aborts, and
        // no reminder keeps retrying the dead end.
        Assert.Equal(WorkflowAgentHandoffDisposition.Failed, failed.Disposition);
        var failedPlan = await reuseHandoff.GetPlanAsync();
        Assert.Equal("agent_session_launch_identity_conflict", failedPlan!.Rejection?.Code);
        Assert.Equal(WorkflowAgentActivationStep.EnsureSession, failedPlan.ActivationStep);
        Assert.Equal(AgentJobStatus.Cancelled,
            await Grains.GetGrain<IAgentJobGrain>(prepared.Invocation!.JobKey).GetStatusAsync());
        var after = await LoadSessionAsync(sessionId);
        var immutableInput = after!.Status.Inputs!.Single(input => input.Id == invocation.InputId);
        var immutableTurn = after.Status.Turns!.Single(turn => turn.Id == invocation.TurnId);
        Assert.Equal(seededInput, immutableInput);
        Assert.Equal(seededTurn.InputIds, immutableTurn.InputIds);
        Assert.Null(immutableTurn.JobId);
        Assert.Equal(AgentTurnStatus.Queued, immutableTurn.Status);
        Assert.Null(immutableTurn.SupersededAt);
        Assert.Null(after.Status.InitialInputOperation);

        await reuseHandoff.TriggerActivationAsync();
        var replay = await reuseHandoff.ActivateAsync();
        Assert.Equal(WorkflowAgentHandoffDisposition.Failed, replay.Disposition);
        var replayPlan = await reuseHandoff.GetPlanAsync();
        Assert.Equal(failedPlan.ActivationError, replayPlan!.ActivationError);
        Assert.Equal("agent_session_launch_identity_conflict", replayPlan.Rejection?.Code);
    }

    [Fact]
    public async Task PartialPreMintedPair_FailsClosedInsteadOfAlreadyPersisted()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition("build", [AgentTask("build", "Build the change", "delivery")], [])
        ]);
        var workflow = await StartWorkflowAsync(
            definition,
            $"workflow-agent-partial-pair-{Guid.NewGuid():N}");
        var runnerId = _runnerId!;
        Assert.Equal(WorkflowAssignmentStatus.Assigned, (await workflow.AssignWorkerAsync(runnerId)).Status);
        Assert.Null(await workflow.ClaimNextAsync(runnerId, TestRunnerGenerationExtensions.ProcessGeneration));
        var run = await LoadRunAsync(_workflowId!);
        var attempt = Assert.Single(run.CurrentStage().Tasks);
        var bootstrapHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(WorkflowAgentHandoffCodec.KeyFor(
            run.Metadata.ProjectId!,
            run.Id,
            run.CurrentStage().Id,
            attempt.Id,
            attempt.WorkId!));
        await bootstrapHandoff.ActivateAsync();
        var bootstrapPlan = await bootstrapHandoff.GetPlanAsync();
        Assert.NotNull(bootstrapPlan?.Invocation);
        var bootstrapWork = (await PollWorkAsync(runnerId)).Work;
        await ReportAsync(runnerId, bootstrapWork, "completed");
        var sessionId = bootstrapPlan!.Invocation!.SessionId;
        await Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand($"runtime-{sessionId}"));

        const string reuseIdentity = "apply-feedback.1";
        var reuseCommand = bootstrapPlan.Command with
        {
            CommandId = reuseIdentity,
            ActionAttemptId = reuseIdentity,
            Prompt = "Apply the feedback",
            ReuseSessionId = sessionId,
            Completion = bootstrapPlan.Command.Completion! with { WorkId = reuseIdentity },
        };
        var invocation = WorkflowAgentHandoffCodec.InvocationFor(reuseCommand);

        // Only the pre-minted Input exists, with content exactly matching
        // the reuse command: the torn pair must still not replay as
        // AlreadyPersisted, because the Turn is missing.
        var sessionGrain = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await TestLifecycle.DeactivateAndWait(sessionGrain, Grains);
        await using (var scope = Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
            var session = await store.LoadAsync(sessionId);
            Assert.NotNull(session);
            session!.Status = session.Status with
            {
                Inputs = [.. session.Status.Inputs!, new AgentSessionInputRecord(
                    Id: invocation.InputId,
                    Sequence: session.Status.Inputs!.Count + 1,
                    Text: reuseCommand.Prompt,
                    Source: "workflow",
                    Acceptance: AgentSessionInputAcceptance.Accepted,
                    RecordedAt: TestTime.UtcDateTime)],
            };
            await store.SaveAsync(sessionId, session);
        }

        var reuseHandoff = Grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(reuseCommand));
        var prepared = await reuseHandoff.PrepareAsync(reuseCommand);
        await reuseHandoff.AcceptAsync(new WorkflowAgentHandoffAcceptance(
            reuseIdentity,
            WorkflowAgentHandoffCodec.Fingerprint(reuseCommand)));
        var failed = await reuseHandoff.ActivateAsync();

        Assert.Equal(WorkflowAgentHandoffDisposition.Failed, failed.Disposition);
        var failedPlan = await reuseHandoff.GetPlanAsync();
        Assert.Equal("agent_session_launch_identity_conflict", failedPlan!.Rejection?.Code);
        Assert.Equal(WorkflowAgentActivationStep.EnsureSession, failedPlan.ActivationStep);
        Assert.Equal(AgentJobStatus.Cancelled,
            await Grains.GetGrain<IAgentJobGrain>(prepared.Invocation!.JobKey).GetStatusAsync());
        var after = await LoadSessionAsync(sessionId);
        Assert.NotNull(after);
        Assert.Null(after!.Status.Turns!.SingleOrDefault(turn => turn.Id == invocation.TurnId));
        var preserved = after.Status.Inputs!.Single(input => input.Id == invocation.InputId);
        Assert.Equal(reuseCommand.Prompt, preserved.Text);
        Assert.Equal("workflow", preserved.Source);
    }

    [Theory]
    [InlineData(AgentTurnStatus.Executing)]
    [InlineData(AgentTurnStatus.Unknown)]
    public void UnresolvedPriorReceipt_BlocksSecondJobRecovery(AgentTurnStatus priorTurnStatus)
    {
        var session = NamedReuseSession(
            out _,
            out var secondOperation,
            priorTurnStatus);

        Assert.Equal("initial_input_operation_conflict", Assert.Throws<InvalidOperationException>(() =>
            session.RecoverInitialAgentJobTurn(
                session.CurrentRuntimeBinding(),
                new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
                secondOperation,
                TestTime.UtcDateTime.AddMinutes(4),
                session.BindingEpoch)).Message);
        Assert.Equal("job-1", session.Status.InitialInputOperation!.JobId);
    }

    [Fact]
    public void AdmittedSameOperationRecovery_StaysProhibited()
    {
        var session = NamedReuseSession(
            out var firstOperation,
            out _,
            AgentTurnStatus.Executing);

        Assert.Equal("initial_input_operation_conflict", Assert.Throws<InvalidOperationException>(() =>
            session.RecoverInitialAgentJobTurn(
                session.CurrentRuntimeBinding(),
                new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
                firstOperation,
                TestTime.UtcDateTime.AddMinutes(4),
                session.BindingEpoch)).Message);
        Assert.True(session.Status.InitialInputOperation!.EffectAdmitted);
    }

    [Theory]
    [InlineData("turn-job")]
    [InlineData("input-job")]
    [InlineData("receipt-input")]
    public void ForgedTerminalOwnership_NeverReleasesThePriorReceipt(string forge)
    {
        var session = NamedReuseSession(
            out _,
            out var secondOperation,
            AgentTurnStatus.Completed);
        session.Status = session.Status with
        {
            Turns = session.Status.Turns!.Select(turn =>
                turn.Id == "turn-1" && forge == "turn-job" ? turn with { JobId = "job-forged" } : turn).ToArray(),
            Inputs = session.Status.Inputs!.Select(input =>
                input.Id == "input-1" && forge == "input-job" ? input with { JobId = null } : input).ToArray(),
            InitialInputOperation = forge == "receipt-input"
                ? session.Status.InitialInputOperation! with { InputId = "input-forged" }
                : session.Status.InitialInputOperation,
        };
        var before = session.Status;

        Assert.Equal("initial_input_start_conflict", Assert.Throws<InvalidOperationException>(() =>
            session.AdmitInitialAgentJobInputEffect(secondOperation, TestTime.UtcDateTime.AddMinutes(4))).Message);
        Assert.Equal("initial_input_operation_conflict", Assert.Throws<InvalidOperationException>(() =>
            session.RecoverInitialAgentJobTurn(
                session.CurrentRuntimeBinding(),
                new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
                secondOperation,
                TestTime.UtcDateTime.AddMinutes(5),
                session.BindingEpoch)).Message);

        Assert.Equal(before.InitialInputOperation, session.Status.InitialInputOperation);
        Assert.Equal(AgentTurnStatus.Queued, session.Status.Turns!.Single(turn => turn.Id == "turn-2").Status);
    }

    [Fact]
    public void TerminalPriorReceipt_LetsSecondJobRecoveryReplaceTheBinding()
    {
        var session = NamedReuseSession(
            out _,
            out var secondOperation,
            AgentTurnStatus.Completed);
        var epochBefore = session.BindingEpoch;

        session.RecoverInitialAgentJobTurn(
            session.CurrentRuntimeBinding(),
            new AgentRuntimeBinding("runner-1", "opencode", "runtime-new"),
            secondOperation,
            TestTime.UtcDateTime.AddMinutes(4),
            epochBefore);

        var receipt = session.Status.InitialInputOperation!;
        Assert.Equal(secondOperation.OperationId, receipt.OperationId);
        Assert.Equal("job-2", receipt.JobId);
        Assert.False(receipt.EffectAdmitted);
        Assert.Equal("runtime-new", session.Status.AgentRuntimeSessionId);
        Assert.Equal(epochBefore + 1, session.BindingEpoch);
        var retargeted = session.Status.Turns!.Single(turn => turn.Id == "turn-2");
        Assert.Equal(AgentTurnStatus.Queued, retargeted.Status);
        Assert.Equal(session.Status.ContextGeneration, retargeted.ContextGeneration);

        // The replacement binding admits the second Job exactly once.
        var admitted = secondOperation with
        {
            RuntimeSessionId = "runtime-new",
            BindingEpoch = session.BindingEpoch,
            ContextGeneration = session.Status.ContextGeneration,
        };
        session.AdmitInitialAgentJobInputEffect(admitted, TestTime.UtcDateTime.AddMinutes(5));
        Assert.True(session.Status.InitialInputOperation!.EffectAdmitted);
        Assert.Equal(AgentTurnStatus.Executing, session.Status.Turns!.Single(turn => turn.Id == "turn-2").Status);
    }

    private static AgentSession NamedReuseSession(
        out AgentInitialInputOperation firstOperation,
        out AgentInitialInputOperation secondOperation,
        AgentTurnStatus priorTurnStatus)
    {
        var session = CreateDomainSession();
        session.EnsureInitialLaunch(
            "input-1", "turn-1", "first prompt", "workflow", "job-1", TestTime.UtcDateTime);
        session.AttachPhysicalSession("runtime-first", null, "/work", null, null, TestTime.UtcDateTime);
        firstOperation = DomainOperation(session, "operation-1", "job-1", "work-1", "input-1", "turn-1", "runtime-first");
        session.AdmitInitialAgentJobInputEffect(firstOperation, TestTime.UtcDateTime.AddMinutes(1));
        if (priorTurnStatus != AgentTurnStatus.Executing)
            session.MarkTurnTerminal("turn-1", priorTurnStatus, null, TestTime.UtcDateTime.AddMinutes(2));
        session.EnsureInitialLaunch(
            "input-2", "turn-2", "second prompt", "workflow", "job-2", TestTime.UtcDateTime.AddMinutes(3));
        secondOperation = DomainOperation(session, "operation-2", "job-2", "work-2", "input-2", "turn-2", "runtime-first");
        return session;
    }

    private static AgentInitialInputOperation DomainOperation(
        AgentSession session,
        string operationId,
        string jobId,
        string workId,
        string inputId,
        string turnId,
        string runtimeSessionId) =>
        new(
            operationId,
            jobId,
            workId,
            "process-1",
            "runner-1",
            inputId,
            turnId,
            "opencode",
            runtimeSessionId,
            session.BindingEpoch,
            session.Status.ContextGeneration,
            TestTime.UtcDateTime);

    private static AgentSession CreateDomainSession() => AgentSession.Create(
        "session-1",
        "runner-1",
        "/work",
        metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/agent-id", "workflow-agent")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "delivery"),
        now: TestTime.UtcDateTime,
        runtime: "opencode");

    private async Task<AgentSession?> LoadSessionAsync(string sessionId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentSessionStore>()
            .LoadAsync(sessionId);
    }

    private static TaskDefinition AgentTask(string id, string title, string session) => new(
        id,
        title,
        "mohist/agent",
        new Dictionary<string, System.Text.Json.JsonElement?>
        {
            ["name"] = Json("mohist/builder"),
            ["session"] = Json(session),
            ["prompt"] = Json(title),
        });

    private static System.Text.Json.JsonElement Json(string value) =>
        System.Text.Json.JsonSerializer.SerializeToElement(value);
}
