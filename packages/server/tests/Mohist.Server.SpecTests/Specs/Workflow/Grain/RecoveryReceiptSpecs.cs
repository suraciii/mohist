using Microsoft.EntityFrameworkCore;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Events;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public sealed class RecoveryReceiptSpecs : WorkflowGrainSpecs
{
    public RecoveryReceiptSpecs(WorkflowGrainFixture fixture) : base(fixture)
    {
    }
    [Fact]
    public async Task RecoveryReceipt_AppliesTerminalResultOnceAndReturnsDurableDuplicateAcknowledgement()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var task = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id,
            work.WorkId,
            runnerId,
            "receipt-session",
            "receipt-turn",
            "opencode",
            "receipt-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var result = new WorkResult("completed", "receipt result");
        var receipt = TerminalReceipt(binding, _workflowId!, result, "receipt-accepted");
        var eventsBefore = (await EventStore.ListAsync(_workflowId!)).Count;

        var first = await workflow.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, first.Status);
        Assert.Equal(receipt.ReceiptId, first.AppliedReceiptId);

        var completed = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(completed.CurrentStage().Tasks).Status);
        Assert.Single(completed.AppliedRecoveryReceipts);
        var eventsAfterApply = (await EventStore.ListAsync(_workflowId!)).Count;

        var duplicate = await workflow.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(first, duplicate);
        Assert.Equal(eventsAfterApply, (await EventStore.ListAsync(_workflowId!)).Count);
        Assert.True(eventsAfterApply > eventsBefore);
    }

    [Fact]
    public async Task RecoveryReceipt_RejectsBindingAndFingerprintMismatchWithoutChangingSettlement()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var task = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id,
            work.WorkId,
            runnerId,
            "receipt-mismatch-session",
            "receipt-mismatch-turn",
            "opencode",
            "receipt-mismatch-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var result = new WorkResult("completed", "receipt result");
        var eventsBefore = (await EventStore.ListAsync(_workflowId!)).Count;
        var bindingMismatch = await workflow.ReceiveRecoveryReceiptAsync(
            TerminalReceipt(
                binding with { AgentTurnId = "other-turn" },
                _workflowId!,
                result,
                "receipt-binding-mismatch"));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, bindingMismatch.Status);

        var fingerprintMismatch = await workflow.ReceiveRecoveryReceiptAsync(
            TerminalReceipt(binding, _workflowId!, result, "receipt-fingerprint-mismatch") with
            {
                Payload = new RuntimeRecoveryReceiptPayload(
                    RuntimeRecoveryReceiptPayloadTypes.TerminalResult,
                    Result: result,
                    Fingerprint: "not-the-result-fingerprint")
            });
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, fingerprintMismatch.Status);

        var unchanged = await LoadRunAsync(_workflowId!);
        var settlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(unchanged.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.AwaitingResult, settlement.State);
        Assert.Empty(unchanged.AppliedRecoveryReceipts);
        Assert.Equal(eventsBefore, (await EventStore.ListAsync(_workflowId!)).Count);
    }

    [Fact]
    public async Task RecoveryReceipt_TerminalResultAfterUpdateFenceSettlesOriginalWorkWithoutReplacement()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var original = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            original.Id,
            work.WorkId,
            runnerId,
            "race-session",
            "race-turn",
            "opencode",
            "race-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var operationId = $"runner-update:race-{Guid.NewGuid():N}";
        var operationGrain = Grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            runnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    _workflowId!,
                    work.WorkId,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            _workflowId!,
            work.WorkId,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(ReportAck.Accepted, await workflow.MarkUpdateInterruptedAsync(
            original.Id,
            work.WorkId,
            runnerId,
            operationId));

        var receipt = TerminalReceipt(
            binding,
            _workflowId!,
            new WorkResult("failed", "runtime returned after update fence", Error: new ExecutionError("turn-failed", "runtime returned after update fence")),
            "race-terminal-result");
        var acknowledgement = await workflow.ReceiveRecoveryReceiptAsync(receipt);

        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var completed = await LoadRunAsync(_workflowId!);
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
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var original = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            original.Id,
            work.WorkId,
            runnerId,
            "replacement-receipt-session",
            "replacement-receipt-turn",
            "opencode",
            "replacement-receipt-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var operationId = $"runner-update:replacement-receipt-{Guid.NewGuid():N}";
        var operationGrain = Grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            runnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    _workflowId!,
                    work.WorkId,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operation.OperationId,
            WorkDispatchOwnerKinds.Workflow,
            _workflowId!,
            work.WorkId,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(
            ReportAck.Accepted,
            await workflow.MarkUpdateInterruptedAsync(original.Id, work.WorkId, runnerId, operation.OperationId));

        var interrupted = await workflow.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, _workflowId!, operation.OperationId));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, interrupted.Status);

        var fenced = await LoadRunAsync(_workflowId!);
        var replacement = Assert.Single(fenced.CurrentStage().Tasks, task => task.Id != original.Id);
        var replacementDispatch = await workflow.ClaimNextAsync(runnerId);
        Assert.NotNull(replacementDispatch);
        Assert.Equal(replacement.WorkId, replacementDispatch!.Id);

        var replacementBinding = new AgentExecutionBinding(
            replacement.Id,
            replacement.WorkId!,
            runnerId,
            "replacement-receipt-session-2",
            "replacement-receipt-turn-2",
            "opencode",
            "replacement-receipt-runtime-session-2");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(replacementBinding));

        var result = new WorkResult("completed", "replacement receipt result");
        var terminal = TerminalReceipt(
            replacementBinding,
            _workflowId!,
            result,
            "replacement-terminal-result") with
        {
            RecoveryGeneration = replacement.RecoveryGeneration,
        };
        var acknowledgement = await workflow.ReceiveRecoveryReceiptAsync(terminal);

        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var recovered = await LoadRunAsync(_workflowId!);
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
    public async Task RecoveryReceipt_TerminalResultReplayRepairsOriginalFenceAfterOperationWriteFailure()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var original = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            original.Id,
            work.WorkId,
            runnerId,
            "replay-original-session",
            "replay-original-turn",
            "opencode",
            "replay-original-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var operationId = $"runner-update:replay-original-{Guid.NewGuid():N}";
        var operationGrain = Grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            runnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    _workflowId!,
                    work.WorkId,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            _workflowId!,
            work.WorkId,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(ReportAck.Accepted, await workflow.MarkUpdateInterruptedAsync(
            original.Id,
            work.WorkId,
            runnerId,
            operationId));

        var receipt = TerminalReceipt(
            binding,
            _workflowId!,
            new WorkResult("failed", "replay after operation failure"),
            "replay-original-terminal");
        _fixture.OperationWriteFailures.FailNext(RunnerUpdateOperationWriteKind.MarkWork);
        await Assert.ThrowsAnyAsync<Exception>(() => workflow.ReceiveRecoveryReceiptAsync(receipt));

        var applied = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(applied.CurrentStage().Tasks).Status);
        Assert.Single(applied.AppliedRecoveryReceipts);
        var beforeReplay = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateWorkStatus.Marked, Assert.Single(beforeReplay!.AffectedWorks).Status);

        var acknowledgement = await workflow.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var settled = await operationGrain.GetAsync(operationId);
        var settledWork = Assert.Single(settled!.AffectedWorks);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, settledWork.Status);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReceiptAcked, settledWork.RecoveryStatus);
    }

    [Fact]
    public async Task RecoveryReceipt_TerminalReplacementReplayRepairsOperationAfterWriteFailure()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var original = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            original.Id,
            work.WorkId,
            runnerId,
            "replay-replacement-session",
            "replay-replacement-turn",
            "opencode",
            "replay-replacement-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var operationId = $"runner-update:replay-replacement-{Guid.NewGuid():N}";
        var operationGrain = Grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            runnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    _workflowId!,
                    work.WorkId,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            _workflowId!,
            work.WorkId,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(ReportAck.Accepted, await workflow.MarkUpdateInterruptedAsync(
            original.Id,
            work.WorkId,
            runnerId,
            operationId));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, (await workflow.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, _workflowId!, operationId))).Status);

        var fenced = await LoadRunAsync(_workflowId!);
        var replacement = Assert.Single(fenced.CurrentStage().Tasks, task => task.Id != original.Id);
        var replacementDispatch = await workflow.ClaimNextAsync(runnerId);
        Assert.NotNull(replacementDispatch);
        var replacementBinding = new AgentExecutionBinding(
            replacement.Id,
            replacement.WorkId!,
            runnerId,
            "replay-replacement-session-2",
            "replay-replacement-turn-2",
            "opencode",
            "replay-replacement-runtime-session-2");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(replacementBinding));

        var terminal = TerminalReceipt(
            replacementBinding,
            _workflowId!,
            new WorkResult("completed", "replay replacement after operation failure"),
            "replay-replacement-terminal") with
        {
            RecoveryGeneration = replacement.RecoveryGeneration,
        };
        _fixture.OperationWriteFailures.FailNext(RunnerUpdateOperationWriteKind.MarkRecoverySettled);
        await Assert.ThrowsAnyAsync<Exception>(() => workflow.ReceiveRecoveryReceiptAsync(terminal));

        var applied = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed,
            Assert.Single(applied.CurrentStage().Tasks, task => task.Id == replacement.Id).Status);
        var beforeReplay = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReceiptAcked,
            Assert.Single(beforeReplay!.AffectedWorks).RecoveryStatus);

        var acknowledgement = await workflow.ReceiveRecoveryReceiptAsync(terminal);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        var settled = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReplacementSettled,
            Assert.Single(settled!.AffectedWorks).RecoveryStatus);
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

    [Fact]
    public async Task RecoveryReceipt_UpdateInterruptionCreatesOneReplacementAndFencesTheOriginalAttempt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var original = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            original.Id,
            work.WorkId,
            runnerId,
            "receipt-interrupt-session",
            "receipt-interrupt-turn",
            "opencode",
            "receipt-interrupt-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var noFence = await workflow.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, _workflowId!, "missing-update-operation"));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, noFence.Status);

        var operationGrain = Grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
        var operation = await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            "runner-update:receipt-test",
            runnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new[]
            {
                new RunnerUpdateWork(
                    WorkDispatchOwnerKinds.Workflow,
                    _workflowId!,
                    work.WorkId,
                    original.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operation.OperationId,
            WorkDispatchOwnerKinds.Workflow,
            _workflowId!,
            work.WorkId,
            original.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(
            ReportAck.Accepted,
            await workflow.MarkUpdateInterruptedAsync(original.Id, work.WorkId, runnerId, operation.OperationId));

        var mismatch = await workflow.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, _workflowId!, "other-update-operation"));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.RejectedMismatch, mismatch.Status);
        var stillFenced = await LoadRunAsync(_workflowId!);
        Assert.Single(stillFenced.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted,
            Assert.Single(stillFenced.CurrentStage().Tasks).AgentResultSettlement!.State);

        var receipt = InterruptedReceipt(binding, _workflowId!, operation.OperationId);
        var first = await workflow.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, first.Status);
        Assert.True(first.IsTerminal);

        var recovered = await LoadRunAsync(_workflowId!);
        var originalAfter = Assert.Single(recovered.CurrentStage().Tasks, task => task.Id == original.Id);
        var replacement = Assert.Single(recovered.CurrentStage().Tasks, task => task.Id != original.Id);
        Assert.Equal(TaskRunStatus.Interrupted, originalAfter.Status);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted, originalAfter.AgentResultSettlement!.State);
        Assert.Equal(binding.AgentTurnId, originalAfter.AgentResultSettlement.AgentTurnId);
        Assert.Equal(1, replacement.RecoveryGeneration);
        Assert.Equal(1, replacement.AgentResultSettlement!.RecoveryGeneration);
        Assert.NotEqual(work.WorkId, replacement.WorkId);
        Assert.NotEqual(binding.AgentTurnId, replacement.AgentResultSettlement.AgentTurnId);
        Assert.Equal(WorkflowRunStatus.Ready, recovered.Status);
        Assert.False(recovered.HasUnresolvedAgentResult());
        Assert.Single(recovered.AppliedRecoveryReceipts);

        var settledOperation = await operationGrain.GetAsync(operation.OperationId);
        Assert.Equal(RunnerUpdateOperationStatus.Settled, settledOperation!.Status);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, Assert.Single(settledOperation.AffectedWorks).Status);

        // The receipt is an at-most-once command. Replaying it returns the
        // durable acknowledgement and cannot add another task attempt.
        var duplicate = await workflow.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(first, duplicate);
        var afterDuplicate = await LoadRunAsync(_workflowId!);
        Assert.Equal(2, afterDuplicate.CurrentStage().Tasks.Count);

        // The replacement is the only eligible dispatch, and it has its own
        // delivery identity.
        var replacementDispatch = await workflow.ClaimNextAsync(runnerId);
        Assert.NotNull(replacementDispatch);
        Assert.Equal(replacement.WorkId, replacementDispatch!.Id);
        Assert.Null(await workflow.ClaimNextAsync(runnerId));

        var newBinding = new AgentExecutionBinding(
            replacement.Id,
            replacement.WorkId!,
            runnerId,
            "replacement-session",
            "replacement-turn",
            "opencode",
            "replacement-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(newBinding));
        var replacementBound = await LoadRunAsync(_workflowId!);
        Assert.Equal("replacement-turn",
            Assert.Single(replacementBound.CurrentStage().Tasks, task => task.Id == replacement.Id).AgentResultSettlement!.AgentTurnId);

        // Old-turn reports and runtime observations cannot settle or mutate the
        // replacement once the original attempt is history.
        Assert.Equal(ReportAck.Stale, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(work.WorkId, TaskReportStatus.Succeeded, null, null, TaskRunId: original.Id)));
        Assert.Equal(ReportAck.Stale, await workflow.ObserveAgentExecutionAsync(
            new AgentExecutionObservation(binding, AgentExecutionObservationKind.Completed, "late-old-turn")));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Stale,
            (await workflow.ReceiveRecoveryReceiptAsync(TerminalReceipt(
                binding,
                _workflowId!,
                new WorkResult("completed", "late old result"),
                "late-old-result"))).Status);

        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            replacement.WorkId!,
            new TaskReport(
                replacement.WorkId!,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: replacement.Id)));
        var completed = await LoadRunAsync(_workflowId!);
        Assert.False(completed.HasUnresolvedAgentResult());
        Assert.Equal(TaskRunStatus.Interrupted,
            Assert.Single(completed.CurrentStage().Tasks, task => task.Id == original.Id).Status);
    }
}
