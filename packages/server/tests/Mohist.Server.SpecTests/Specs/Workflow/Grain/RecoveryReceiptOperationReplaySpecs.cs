using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public sealed class RecoveryReceiptOperationReplaySpecs : WorkflowGrainSpecs
{
    public RecoveryReceiptOperationReplaySpecs(WorkflowGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task SessionInterruptionDeliveryFailure_IsRepairedByIdempotentOwnerRetry()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var original = Assert.Single((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks);
        var sessionId = $"session-repair-{Guid.NewGuid():N}";
        var turnId = $"turn-repair-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            $"input-{Guid.NewGuid():N}",
            turnId,
            "repair session visibility",
            "workflow-test",
            $"job-{Guid.NewGuid():N}",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mohist.io/project-id"] = TestProjectId(_workflowId!),
                ["mohist.io/source-kind"] = "workflow",
                ["mohist.io/source-id"] = _workflowId!,
                ["mohist.io/session-name"] = sessionId,
            }),
            Runtime: "opencode"));

        var binding = new AgentExecutionBinding(
            original.Id,
            work.WorkId,
            runnerId,
            sessionId,
            turnId,
            "opencode",
            "repair-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var operationId = $"runner-update:session-repair-{Guid.NewGuid():N}";
        var operationGrain = Grains.GetGrain<IRunnerUpdateOperationGrain>(runnerId);
        await operationGrain.StartOrGetAsync(new RunnerUpdateOperation(
            operationId,
            runnerId,
            _fixture.TimeProvider.GetUtcNow(),
            new[] { new RunnerUpdateWork(
                WorkDispatchOwnerKinds.Workflow,
                _workflowId!,
                work.WorkId,
                original.Id,
                WorkItemTypes.Task) }));
        await operationGrain.MarkWorkAsync(
            operationId,
            WorkDispatchOwnerKinds.Workflow,
            _workflowId!,
            work.WorkId,
            original.Id,
            RunnerUpdateWorkStatus.Marked);

        _fixture.SessionStatePersistence.QueueFailures(1);
        await Assert.ThrowsAnyAsync<Exception>(() => workflow.MarkUpdateInterruptedAsync(
            original.Id,
            work.WorkId,
            runnerId,
            operationId));

        var fenced = await LoadRunAsync(_workflowId!);
        Assert.NotEmpty(fenced.PendingSessionInterruptionDeliveries);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted,
            Assert.Single(fenced.CurrentStage().Tasks).AgentResultSettlement!.State);

        Assert.Equal(ReportAck.Accepted, await workflow.MarkUpdateInterruptedAsync(
            original.Id,
            work.WorkId,
            runnerId,
            operationId));

        var repaired = await session.GetAsync();
        Assert.NotNull(repaired);
        Assert.Equal(AgentWorkInterruptionStates.Interrupted,
            Assert.Single(repaired!.InterruptionHistory!).State);
        var repairedTurn = Assert.Single(await session.ListTurnsAsync());
        Assert.Equal(AgentWorkInterruptionStates.Interrupted, repairedTurn.Interruption?.State);
        Assert.Empty((await LoadRunAsync(_workflowId!)).PendingSessionInterruptionDeliveries);

        var interruptedReceipt = InterruptedReceipt(binding, _workflowId!, operationId);
        _fixture.SessionStatePersistence.QueueFailures(1);
        await Assert.ThrowsAnyAsync<Exception>(() => workflow.ReceiveRecoveryReceiptAsync(interruptedReceipt));

        var replacementRun = await LoadRunAsync(_workflowId!);
        var replacement = Assert.Single(
            replacementRun.CurrentStage().Tasks,
            task => task.Id != original.Id);
        Assert.Equal(AgentWorkInterruptionStates.Recovering, replacement.Interruption?.State);
        Assert.NotEmpty(replacementRun.PendingSessionInterruptionDeliveries);

        var replacementAcknowledgement = await workflow.ReceiveRecoveryReceiptAsync(interruptedReceipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, replacementAcknowledgement.Status);
        var recoveringSession = await session.GetAsync();
        Assert.Equal(2, recoveringSession!.InterruptionHistory!.Count);
        Assert.Equal(AgentWorkInterruptionStates.Recovering,
            recoveringSession.InterruptionHistory.Single(item => item.RecoveryGeneration == 1).State);

        var replacementWork = await workflow.ClaimNextAsync(runnerId);
        Assert.NotNull(replacementWork);
        var replacementState = await LoadRunAsync(_workflowId!);
        var replacementTask = Assert.Single(
            replacementState.CurrentStage().Tasks,
            task => task.Id != original.Id);
        var replacementSettlement = replacementTask.AgentResultSettlement!;
        var replacementBinding = new AgentExecutionBinding(
            replacementTask.Id,
            replacementTask.WorkId!,
            runnerId,
            sessionId,
            replacementSettlement.AgentTurnId!,
            "opencode",
            "repair-runtime-session-replacement");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(replacementBinding));

        var terminal = TerminalReceipt(
            replacementBinding,
            _workflowId!,
            new WorkResult("completed", "recovered after Session retry"),
            "session-repair-terminal") with
        {
            RecoveryGeneration = replacementTask.RecoveryGeneration,
        };
        _fixture.SessionStatePersistence.QueueFailures(1);
        await Assert.ThrowsAnyAsync<Exception>(() => workflow.ReceiveRecoveryReceiptAsync(terminal));
        Assert.NotEmpty((await LoadRunAsync(_workflowId!)).PendingSessionInterruptionDeliveries);

        var terminalReplay = await workflow.ReceiveRecoveryReceiptAsync(terminal);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, terminalReplay.Status);
        var recoveredSession = await session.GetAsync();
        var recoveredHistory = recoveredSession!.InterruptionHistory!;
        Assert.Equal(AgentWorkInterruptionStates.Recovered,
            recoveredHistory.Single(item => item.RecoveryGeneration == 1).State);
    }

    [Fact]
    public async Task TerminalResultReplayRepairsOriginalFenceAfterOperationWriteFailure()
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
    public async Task InterruptedReceiptDoesNotAcknowledgeBeforeReplacementCommitAndReplayRepairsFence()
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
            "replay-interrupted-session",
            "replay-interrupted-turn",
            "opencode",
            "replay-interrupted-runtime-session");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var operationId = $"runner-update:replay-interrupted-{Guid.NewGuid():N}";
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

        var receipt = InterruptedReceipt(binding, _workflowId!, operationId);
        _fixture.OperationWriteFailures.FailNext(RunnerUpdateOperationWriteKind.MarkWork);
        await Assert.ThrowsAnyAsync<Exception>(() => workflow.ReceiveRecoveryReceiptAsync(receipt));

        var committed = await LoadRunAsync(_workflowId!);
        var interrupted = Assert.Single(committed.CurrentStage().Tasks, task => task.Id == original.Id);
        var replacement = Assert.Single(committed.CurrentStage().Tasks, task => task.Id != original.Id);
        Assert.Equal(TaskRunStatus.Interrupted, interrupted.Status);
        Assert.Equal(AgentResultSettlementState.RecoverablyInterrupted, interrupted.AgentResultSettlement!.State);
        Assert.Equal(binding.AgentTurnId, interrupted.AgentResultSettlement.AgentTurnId);
        Assert.Equal(1, replacement.RecoveryGeneration);
        Assert.Single(committed.AppliedRecoveryReceipts);
        Assert.Equal(RunnerUpdateWorkStatus.Marked,
            Assert.Single((await operationGrain.GetAsync(operationId))!.AffectedWorks).Status);

        var acknowledgement = await workflow.ReceiveRecoveryReceiptAsync(receipt);
        Assert.Equal(RuntimeRecoveryReceiptAckStatuses.Accepted, acknowledgement.Status);
        Assert.Equal(receipt.ReceiptId, acknowledgement.AppliedReceiptId);
        var afterReplay = await LoadRunAsync(_workflowId!);
        Assert.Equal(2, afterReplay.CurrentStage().Tasks.Count);
        var settled = await operationGrain.GetAsync(operationId);
        Assert.Equal(RunnerUpdateWorkStatus.Settled, Assert.Single(settled!.AffectedWorks).Status);
        Assert.Equal(RunnerUpdateRecoveryStatus.ReceiptAcked,
            Assert.Single(settled.AffectedWorks).RecoveryStatus);
    }

    [Fact]
    public async Task TerminalReplacementReplayRepairsOperationAfterWriteFailure()
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
}
