using Microsoft.EntityFrameworkCore;
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
    public async Task RecoveryReceipt_UpdateInterruptionRequiresFenceAndRemainsRetryableUntilReplacementArbitration()
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
                    task.Id,
                    WorkItemTypes.Task)
            }));
        await operationGrain.MarkWorkAsync(
            operation.OperationId,
            WorkDispatchOwnerKinds.Workflow,
            _workflowId!,
            work.WorkId,
            task.Id,
            RunnerUpdateWorkStatus.Marked);
        Assert.Equal(
            ReportAck.Accepted,
            await workflow.MarkUpdateInterruptedAsync(task.Id, work.WorkId, runnerId, operation.OperationId));

        var retryable = await workflow.ReceiveRecoveryReceiptAsync(
            InterruptedReceipt(binding, _workflowId!, operation.OperationId));
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Retryable, retryable.Status);
        Assert.False(retryable.IsTerminal);
        var interrupted = await LoadRunAsync(_workflowId!);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted,
            Assert.Single(interrupted.CurrentStage().Tasks).AgentResultSettlement!.State);
        Assert.Empty(interrupted.AppliedRecoveryReceipts);
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
