using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Recovery-receipt arbitration of the workflow run: at-most-once terminal
/// application, deadline-blocked boundaries, identity/fingerprint fencing,
/// and update-interruption replacement coordination with the runner update
/// operation state. Drives the real grains without a cluster; the
/// runner-update-operation grain participates through the shared registry so
/// workflow-side settlements are visible to direct assertions (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainRecoveryReceiptSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainRecoveryReceiptSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecoveryReceipt_AppliesTerminalResultOnceAndReturnsDurableDuplicateAcknowledgement()
    {
        var (arrangement, work, task, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-once");

        var result = new WorkResult("completed", "receipt result");
        var receipt = TerminalReceipt(binding, arrangement.RunId, result, "receipt-accepted");
        var eventsBefore = (await arrangement.Events.ListAsync(arrangement.RunId)).Count;

        var first = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, first.Status);
        Assert.Equal(receipt.ReceiptId, first.AppliedReceiptId);

        var completed = await RequireRunAsync(arrangement);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(completed.CurrentStage().Tasks).Status);
        Assert.Single(completed.AppliedRecoveryReceipts);
        var eventsAfterApply = (await arrangement.Events.ListAsync(arrangement.RunId)).Count;

        var duplicate = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(first, duplicate);
        Assert.Equal(eventsAfterApply, (await arrangement.Events.ListAsync(arrangement.RunId)).Count);
        Assert.True(eventsAfterApply > eventsBefore);
    }

    [Fact]
    public async Task RecoveryReceipt_AtDeadlineCommitsBlockedBoundaryBeforeApplyingResult()
    {
        var (arrangement, _, _, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-deadline");
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected")));

        var unknown = await RequireRunAsync(arrangement);
        var deadline = Assert.IsType<DateTimeOffset>(
            Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement!.DeadlineAt);
        TimeProvider.Advance(deadline - TimeProvider.GetUtcNow());

        var receipt = TerminalReceipt(
            binding,
            arrangement.RunId,
            new WorkResult("completed", "late authoritative result"),
            "receipt-deadline-terminal");
        var acknowledgement = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);

        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var settled = await RequireRunAsync(arrangement);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(settled.CurrentStage().Tasks).Status);
        var eventTypes = (await arrangement.Events.ListAsync(arrangement.RunId))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.Single(eventTypes, type => type == EventCatalog.ReverseDns.TaskBlocked);
        Assert.Contains(EventCatalog.ReverseDns.TaskCompleted, eventTypes);
    }

    [Fact]
    public async Task RecoveryReceipt_RejectsBindingAndFingerprintMismatchWithoutChangingSettlement()
    {
        var (arrangement, _, _, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-mismatch");

        var result = new WorkResult("completed", "receipt result");
        var eventsBefore = (await arrangement.Events.ListAsync(arrangement.RunId)).Count;
        var bindingMismatch = await arrangement.Grain.ReceiveRecoveryReceiptAsync(
            TerminalReceipt(
                binding with { AgentTurnId = "other-turn" },
                arrangement.RunId,
                result,
                "receipt-binding-mismatch"));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, bindingMismatch.Status);

        var fingerprintMismatch = await arrangement.Grain.ReceiveRecoveryReceiptAsync(
            TerminalReceipt(binding, arrangement.RunId, result, "receipt-fingerprint-mismatch") with
            {
                Payload = new RuntimeRecoveryReceiptPayload(
                    RuntimeRecoveryReceiptPayloadTypes.TerminalResult,
                    Result: result,
                    Fingerprint: "not-the-result-fingerprint")
            });
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, fingerprintMismatch.Status);

        var unchanged = await RequireRunAsync(arrangement);
        var settlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(unchanged.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.AwaitingResult, settlement.State);
        Assert.Empty(unchanged.AppliedRecoveryReceipts);
        Assert.Equal(eventsBefore, (await arrangement.Events.ListAsync(arrangement.RunId)).Count);
    }

    [Fact]
    public async Task RecoveryReceipt_TerminalResultAfterUpdateFenceSettlesOriginalWorkWithoutReplacement()
    {
        var (arrangement, work, original, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-fence-settle");

        var operationId = $"runner-update:fence-settle-{Guid.NewGuid():N}";
        var operationGrain = arrangement.Operations!.For(arrangement.WorkerId);
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            arrangement.WorkerId,
            TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    arrangement.RunId,
                    work.Id!,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            arrangement.RunId,
            work.Id!,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.MarkUpdateInterruptedAsync(
            original.Id,
            work.Id!,
            arrangement.WorkerId,
            operationId));

        var receipt = TerminalReceipt(
            binding,
            arrangement.RunId,
            new WorkResult("failed", "runtime returned after update fence", Error: new ExecutionError("turn-failed", "runtime returned after update fence")),
            "race-terminal-result");
        var acknowledgement = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);

        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var completed = await RequireRunAsync(arrangement);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(completed.CurrentStage().Tasks).Status);
        Assert.Single(completed.CurrentStage().Tasks);
        Assert.False(completed.HasUnresolvedAgentResult());
        var settledOperation = await operationGrain.GetAsync(operationId);
        var settledWork = Assert.Single(settledOperation!.AffectedWorks);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, settledWork.Status);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReceiptAcked, settledWork.RecoveryStatus);
    }

    [Fact]
    public async Task RecoveryReceipt_TerminalResultForReplacementProjectsRecoveredAndSettlesReplacement()
    {
        var (arrangement, work, original, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-replacement");
        var operationGrain = arrangement.Operations!.For(arrangement.WorkerId);

        var operationId = $"runner-update:replacement-receipt-{Guid.NewGuid():N}";
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            arrangement.WorkerId,
            TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    arrangement.RunId,
                    work.Id!,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operation.OperationId,
            WorkDispatchOwnerKinds.Workflow,
            arrangement.RunId,
            work.Id!,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(
            ReportAck.Accepted,
            await arrangement.Grain.MarkUpdateInterruptedAsync(original.Id, work.Id!, arrangement.WorkerId, operation.OperationId));

        var interrupted = await arrangement.Grain.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, arrangement.RunId, operation.OperationId));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, interrupted.Status);

        var fenced = await RequireRunAsync(arrangement);
        var replacement = Assert.Single(fenced.CurrentStage().Tasks, task => task.Id != original.Id);
        var replacementDispatch = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(replacementDispatch);
        Assert.Equal(replacement.WorkId, replacementDispatch!.Id);

        var replacementBinding = new AgentExecutionBinding(
            replacement.Id,
            replacement.WorkId!,
            arrangement.WorkerId,
            "replacement-receipt-session-2",
            "replacement-receipt-turn-2",
            "opencode",
            "replacement-receipt-runtime-session-2");
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.BindAgentExecutionAsync(replacementBinding));

        var terminal = TerminalReceipt(
            replacementBinding,
            arrangement.RunId,
            new WorkResult("completed", "replacement receipt result"),
            "replacement-terminal-result") with
        {
            RecoveryGeneration = replacement.RecoveryGeneration,
        };
        var acknowledgement = await arrangement.Grain.ReceiveRecoveryReceiptAsync(terminal);

        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var recovered = await RequireRunAsync(arrangement);
        var recoveredReplacement = Assert.Single(recovered.CurrentStage().Tasks, task => task.Id == replacement.Id);
        Assert.Equal(TaskRunStatus.Completed, recoveredReplacement.Status);
        Assert.Equal(AgentWorkInterruptionStates.Recovered, recoveredReplacement.AgentInterruption?.State);
        Assert.Equal(operationId, recoveredReplacement.AgentInterruption?.UpdateOperationId);
        Assert.False(recovered.HasUnresolvedAgentResult());

        var settledOperation = await operationGrain.GetAsync(operationId);
        var settledWork = Assert.Single(settledOperation!.AffectedWorks);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, settledWork.Status);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReplacementSettled, settledWork.RecoveryStatus);
    }

    [Fact]
    public async Task RecoveryReceipt_UpdateInterruptionCreatesOneReplacementAndFencesTheOriginalAttempt()
    {
        var (arrangement, work, original, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-interrupt");
        var operationGrain = arrangement.Operations!.For(arrangement.WorkerId);

        var noFence = await arrangement.Grain.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, arrangement.RunId, "missing-update-operation"));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, noFence.Status);

        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            "runner-update:receipt-test",
            arrangement.WorkerId,
            TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    arrangement.RunId,
                    work.Id!,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operation.OperationId,
            WorkDispatchOwnerKinds.Workflow,
            arrangement.RunId,
            work.Id!,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(
            ReportAck.Accepted,
            await arrangement.Grain.MarkUpdateInterruptedAsync(original.Id, work.Id!, arrangement.WorkerId, operation.OperationId));

        var mismatch = await arrangement.Grain.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, arrangement.RunId, "other-update-operation"));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, mismatch.Status);
        var stillFenced = await RequireRunAsync(arrangement);
        Assert.Single(stillFenced.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted,
            Assert.Single(stillFenced.CurrentStage().Tasks).AgentResultSettlement!.State);

        var receipt = InterruptedReceipt(binding, arrangement.RunId, operation.OperationId);
        var first = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, first.Status);
        Assert.True(first.IsTerminal);

        var recovered = await RequireRunAsync(arrangement);
        var originalAfter = Assert.Single(recovered.CurrentStage().Tasks, task => task.Id == original.Id);
        var replacement = Assert.Single(recovered.CurrentStage().Tasks, task => task.Id != original.Id);
        Assert.Equal(TaskRunStatus.Interrupted, originalAfter.Status);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted, originalAfter.AgentResultSettlement!.State);
        Assert.Equal(binding.AgentTurnId, originalAfter.AgentResultSettlement.AgentTurnId);
        Assert.Equal(1, replacement.RecoveryGeneration);
        Assert.Equal(1, replacement.AgentResultSettlement!.RecoveryGeneration);
        Assert.NotEqual(work.Id, replacement.WorkId);
        Assert.NotEqual(binding.AgentTurnId, replacement.AgentResultSettlement.AgentTurnId);
        Assert.Equal(WorkflowRunStatus.Ready, recovered.Status);
        Assert.False(recovered.HasUnresolvedAgentResult());
        Assert.Single(recovered.AppliedRecoveryReceipts);

        var settledOperation = await operationGrain.GetAsync(operation.OperationId);
        Assert.Equal(RunnerUpdateOperationStatus.Settled, settledOperation!.Status);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, Assert.Single(settledOperation.AffectedWorks).Status);

        // The receipt is an at-most-once command. Replaying it returns the
        // durable acknowledgement and cannot add another task attempt.
        var duplicate = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(first, duplicate);
        var afterDuplicate = await RequireRunAsync(arrangement);
        Assert.Equal(2, afterDuplicate.CurrentStage().Tasks.Count);

        // The replacement is the only eligible dispatch, and it has its own
        // delivery identity.
        var replacementDispatch = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(replacementDispatch);
        Assert.Equal(replacement.WorkId, replacementDispatch!.Id);
        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId));

        var newBinding = new AgentExecutionBinding(
            replacement.Id,
            replacement.WorkId!,
            arrangement.WorkerId,
            "replacement-session",
            "replacement-turn",
            "opencode",
            "replacement-runtime-session");
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.BindAgentExecutionAsync(newBinding));
        var replacementBound = await RequireRunAsync(arrangement);
        Assert.Equal("replacement-turn",
            Assert.Single(replacementBound.CurrentStage().Tasks, task => task.Id == replacement.Id).AgentResultSettlement!.AgentTurnId);

        // Old-turn reports and runtime observations cannot settle or mutate the
        // replacement once the original attempt is history.
        Assert.Equal(ReportAck.Stale, await arrangement.Grain.ReceiveTaskReportAsync(
            arrangement.WorkerId,
            work.Id!,
            new TaskReport(work.Id!, TaskReportStatus.Succeeded, null, null, TaskRunId: original.Id)));
        Assert.Equal(ReportAck.Stale, await arrangement.Grain.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(binding, AgentExecutionObservationKind.Completed, "late-old-turn")));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Stale,
            (await arrangement.Grain.ReceiveRecoveryReceiptAsync(TerminalReceipt(
                binding,
                arrangement.RunId,
                new WorkResult("completed", "late old result"),
                "late-old-result"))).Status);

        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.ReceiveTaskReportAsync(
            arrangement.WorkerId,
            replacement.WorkId!,
            new TaskReport(
                replacement.WorkId!,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: replacement.Id)));
        var completed = await RequireRunAsync(arrangement);
        Assert.False(completed.HasUnresolvedAgentResult());
        Assert.Equal(TaskRunStatus.Interrupted,
            Assert.Single(completed.CurrentStage().Tasks, task => task.Id == original.Id).Status);
    }

    [Fact]
    public async Task TerminalResultReplayRepairsOriginalFenceAfterOperationWriteFailure()
    {
        var (arrangement, work, original, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-replay-original");
        var operationGrain = arrangement.Operations!.For(arrangement.WorkerId);

        var operationId = $"runner-update:replay-original-{Guid.NewGuid():N}";
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            arrangement.WorkerId,
            TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    arrangement.RunId,
                    work.Id!,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            arrangement.RunId,
            work.Id!,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.MarkUpdateInterruptedAsync(
            original.Id,
            work.Id!,
            arrangement.WorkerId,
            operationId));

        var receipt = TerminalReceipt(
            binding,
            arrangement.RunId,
            new WorkResult("failed", "replay after operation failure"),
            "replay-original-terminal");
        arrangement.Operations.Probe.FailNext(RunnerUpdateOperationWriteKind.MarkWork);
        await Assert.ThrowsAnyAsync<Exception>(() => arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt));

        var applied = await RequireRunAsync(arrangement);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(applied.CurrentStage().Tasks).Status);
        Assert.Single(applied.AppliedRecoveryReceipts);
        var beforeReplay = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateWorkStatus.Marked, Assert.Single(beforeReplay!.AffectedWorks).Status);

        var acknowledgement = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var settled = await operationGrain.GetAsync(operationId);
        var settledWork = Assert.Single(settled!.AffectedWorks);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, settledWork.Status);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReceiptAcked, settledWork.RecoveryStatus);
    }

    [Fact]
    public async Task InterruptedReceiptDoesNotAcknowledgeBeforeReplacementCommitAndReplayRepairsFence()
    {
        var (arrangement, work, original, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-replay-interrupted");
        var operationGrain = arrangement.Operations!.For(arrangement.WorkerId);

        var operationId = $"runner-update:replay-interrupted-{Guid.NewGuid():N}";
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            arrangement.WorkerId,
            TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    arrangement.RunId,
                    work.Id!,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            arrangement.RunId,
            work.Id!,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.MarkUpdateInterruptedAsync(
            original.Id,
            work.Id!,
            arrangement.WorkerId,
            operationId));

        var receipt = InterruptedReceipt(binding, arrangement.RunId, operationId);
        arrangement.Operations.Probe.FailNext(RunnerUpdateOperationWriteKind.MarkWork);
        await Assert.ThrowsAnyAsync<Exception>(() => arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt));

        var committed = await RequireRunAsync(arrangement);
        var interrupted = Assert.Single(committed.CurrentStage().Tasks, task => task.Id == original.Id);
        var replacement = Assert.Single(committed.CurrentStage().Tasks, task => task.Id != original.Id);
        Assert.Equal(TaskRunStatus.Interrupted, interrupted.Status);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted, interrupted.AgentResultSettlement!.State);
        Assert.Equal(binding.AgentTurnId, interrupted.AgentResultSettlement.AgentTurnId);
        Assert.Equal(1, replacement.RecoveryGeneration);
        Assert.Single(committed.AppliedRecoveryReceipts);
        Assert.Equal(RunnerUpdateWorkStatus.Marked,
            Assert.Single((await operationGrain.GetAsync(operationId))!.AffectedWorks).Status);

        var acknowledgement = await arrangement.Grain.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        Assert.Equal(receipt.ReceiptId, acknowledgement.AppliedReceiptId);
        var afterReplay = await RequireRunAsync(arrangement);
        Assert.Equal(2, afterReplay.CurrentStage().Tasks.Count);
        var settled = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, Assert.Single(settled!.AffectedWorks).Status);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReceiptAcked,
            Assert.Single(settled.AffectedWorks).RecoveryStatus);
    }

    [Fact]
    public async Task TerminalReplacementReplayRepairsOperationAfterWriteFailure()
    {
        var (arrangement, work, original, binding) = await ArrangeWithBoundAgentAsync("wr-receipt-replay-replacement");
        var operationGrain = arrangement.Operations!.For(arrangement.WorkerId);

        var operationId = $"runner-update:replay-replacement-{Guid.NewGuid():N}";
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            arrangement.WorkerId,
            TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    arrangement.RunId,
                    work.Id!,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            arrangement.RunId,
            work.Id!,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.MarkUpdateInterruptedAsync(
            original.Id,
            work.Id!,
            arrangement.WorkerId,
            operationId));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, (await arrangement.Grain.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, arrangement.RunId, operationId))).Status);

        var fenced = await RequireRunAsync(arrangement);
        var replacement = Assert.Single(fenced.CurrentStage().Tasks, task => task.Id != original.Id);
        var replacementDispatch = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(replacementDispatch);
        var replacementBinding = new AgentExecutionBinding(
            replacement.Id,
            replacement.WorkId!,
            arrangement.WorkerId,
            "replay-replacement-session-2",
            "replay-replacement-turn-2",
            "opencode",
            "replay-replacement-runtime-session-2");
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.BindAgentExecutionAsync(replacementBinding));

        var terminal = TerminalReceipt(
            replacementBinding,
            arrangement.RunId,
            new WorkResult("completed", "replay replacement after operation failure"),
            "replay-replacement-terminal") with
        {
            RecoveryGeneration = replacement.RecoveryGeneration,
        };
        arrangement.Operations.Probe.FailNext(RunnerUpdateOperationWriteKind.MarkRecoverySettled);
        await Assert.ThrowsAnyAsync<Exception>(() => arrangement.Grain.ReceiveRecoveryReceiptAsync(terminal));

        var applied = await RequireRunAsync(arrangement);
        Assert.Equal(TaskRunStatus.Completed,
            Assert.Single(applied.CurrentStage().Tasks, task => task.Id == replacement.Id).Status);
        var beforeReplay = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReceiptAcked,
            Assert.Single(beforeReplay!.AffectedWorks).RecoveryStatus);

        var acknowledgement = await arrangement.Grain.ReceiveRecoveryReceiptAsync(terminal);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var settled = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReplacementSettled,
            Assert.Single(settled!.AffectedWorks).RecoveryStatus);
    }

    /// <summary>
    /// Starts a single agent-task run, assigns the worker, claims the work,
    /// and binds the agent execution. Returns the claimed dispatch together
    /// with the persisted task-run so receipts can reference full identity.
    /// </summary>
    private async Task<(
        WorkflowGrainArrangement Arrangement,
        WorkItem Work,
        TaskRun Task,
        AgentExecutionBinding Binding)>
        ArrangeWithBoundAgentAsync(string runId)
    {
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            runId,
            SingleAgentStage(),
            TimeProvider);

        await arrangement.Grain.AssignWorkerAsync(arrangement.WorkerId);
        var work = await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId);
        Assert.NotNull(work);

        var run = await RequireRunAsync(arrangement);
        var task = Assert.Single(run.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id,
            work!.Id!,
            arrangement.WorkerId,
            $"{runId}-session",
            $"{runId}-turn",
            "opencode",
            $"{runId}-runtime-session");
        Assert.Equal(ReportAck.Accepted, await arrangement.Grain.BindAgentExecutionAsync(binding));
        // The reconciliation path persists a delivery snapshot when work is
        // dispatched; mirror that fact so receipt validation sees the same
        // durable state a cluster poll would have produced.
        await arrangement.Snapshots.SaveFirstJsonAsync(arrangement.RunId, binding.WorkId, "{}");
        return (arrangement, work, task, binding);
    }

    private static RuntimeRecoveryReceipt TerminalReceipt(
        AgentExecutionBinding binding,
        string workflowRunId,
        WorkResult result,
        string receiptId) => new(
            workflowRunId,
            binding.TaskRunId,
            binding.WorkId,
            binding.RunnerId,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId,
            RecoveryGeneration: 0,
            ReceiptId: receiptId,
            Payload: new RuntimeRecoveryReceiptPayload(
                RuntimeRecoveryReceiptPayloadTypes.TerminalResult,
                Result: result,
                Fingerprint: RuntimeRecoveryReceiptFingerprint.For(result)));

    private static RuntimeRecoveryReceipt InterruptedReceipt(
        AgentExecutionBinding binding,
        string workflowRunId,
        string operationId) => new(
            workflowRunId,
            binding.TaskRunId,
            binding.WorkId,
            binding.RunnerId,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId,
            RecoveryGeneration: 0,
            ReceiptId: $"receipt-interrupted-{operationId}",
            Payload: new RuntimeRecoveryReceiptPayload(
                RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted,
                UpdateOperationId: operationId,
                StopConfirmed: true));

    private static WorkflowDefinition SingleAgentStage() => new(
    [
        new StageDefinition(
            "build",
            [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            []),
    ]);

    private static async Task<WorkflowRun> RequireRunAsync(WorkflowGrainArrangement arrangement) =>
        await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");
}
