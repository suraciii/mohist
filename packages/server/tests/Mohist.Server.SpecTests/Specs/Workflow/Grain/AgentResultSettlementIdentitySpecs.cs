using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public sealed partial class AgentResultSettlementSpecs
{
    [Fact]
    public async Task TerminalAgentReplay_RequiresTheFrozenBindingAndOriginalResult()
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
            "session-terminal-replay",
            "turn-terminal-replay",
            "opencode",
            "runtime-terminal-replay");
        var service = Services.GetRequiredService<WorkflowReportService>();
        var result = new WorkResult(
            "completed",
            "authoritative result",
            Output: System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true }),
            ExitCode: 0);

        Assert.Equal(WorkReportVerdict.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal("accepted", (await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            result,
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId)).Ack);
        var eventCount = (await EventStore.ListAsync(_workflowId!)).Count;

        var wrongBinding = binding with { AgentTurnId = "wrong-turn" };
        Assert.Equal("refused", (await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            result,
            CancellationToken.None,
            wrongBinding.AgentSessionId,
            wrongBinding.AgentTurnId,
            wrongBinding.Runtime,
            wrongBinding.RuntimeSessionId)).Ack);

        Assert.Equal("refused", (await service.ReportAsync(
            runnerId,
            _workflowId!,
            work.WorkId,
            work.TaskRunId,
            result with { Message = "conflicting result" },
            CancellationToken.None,
            binding.AgentSessionId,
            binding.AgentTurnId,
            binding.Runtime,
            binding.RuntimeSessionId)).Ack);

        Assert.Equal(eventCount, (await EventStore.ListAsync(_workflowId!)).Count);
        var completed = await LoadRunAsync(_workflowId!);
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Null(completedTask.AgentResultSettlement);
        Assert.Equal(binding, completedTask.TerminalExecutionBinding);
    }

    [Fact]
    public async Task BlockedSettlement_FencesMismatchedLateReportLeavingBlockedStateUntouched()
    {
        // Issue-628 T-005: any late authoritative report whose
        // taskRunId/workId/runnerId tuple does not match the durably
        // Blocked attempt must be rejected as stale without reviving or
        // clearing the blocked settlement, and must not alter Runner
        // projections. Only an identity-matching authoritative report may
        // settle the attempt.
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/opencode")],
            checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var (work, _) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            task.Id, work.WorkId, runnerId, "session-mismatch", "turn-mismatch", "opencode", "runtime-mismatch");
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed");

        Assert.Equal(WorkReportVerdict.Accepted, await workflow.BindAgentExecutionAsync(binding));
        Assert.Equal(WorkReportVerdict.Accepted, await workflow.ObserveAgentExecutionAsync(observation));

        var unknown = await LoadRunAsync(_workflowId!);
        var deadline = Assert.IsType<DateTimeOffset>(
            Assert.Single(unknown.CurrentStage().Tasks).AgentResultSettlement!.DeadlineAt);
        await AdvanceToAgentSettlementDeadlineAsync(deadline);
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        var blockedTask = Assert.Single(blocked.CurrentStage().Tasks);
        Assert.Equal(AgentResultSettlementState.Blocked, blockedTask.AgentResultSettlement!.State);
        var beforeEventCount = (await EventStore.ListAsync(_workflowId!)).Count;

        // Mismatched taskRunId -> stale.
        Assert.Equal(WorkReportVerdict.Refused, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: "other-task.1")));
        // Mismatched workId -> stale.
        Assert.Equal(WorkReportVerdict.Refused, await workflow.ReceiveTaskReportAsync(
            runnerId,
            "other-work",
            new TaskReport(
                "other-work",
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: task.Id)));
        // Mismatched runnerId -> stale (ForeignRunner fence).
        Assert.Equal(WorkReportVerdict.Refused, await workflow.ReceiveTaskReportAsync(
            "other-runner",
            work.WorkId,
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: task.Id)));

        var afterMismatches = await LoadRunAsync(_workflowId!);
        var afterTask = Assert.Single(afterMismatches.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, afterTask.Status);
        Assert.Equal(AgentResultSettlementState.Blocked, afterTask.AgentResultSettlement!.State);
        Assert.Equal(task.Id, afterTask.Id);
        Assert.Equal(work.WorkId, afterTask.WorkId);
        Assert.Equal(runnerId, afterTask.WorkerId);
        var eventTypes = (await EventStore.ListAsync(_workflowId!))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskCompleted, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);
        Assert.Equal(beforeEventCount, (await EventStore.ListAsync(_workflowId!)).Count);

        // Runner projections must still omit the blocked run.
        var dispatch = Services.GetRequiredService<DispatchService>();
        var querier = Services.GetRequiredService<WorkflowRunQuerier>();
        Assert.Empty(await querier.FindRunningAssignedToAsync(runnerId));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync(runnerId));
        Assert.DoesNotContain((await runner.GetRuntimeStateAsync()).ActiveWorks, item =>
            string.Equals(item.OwnerId, _workflowId, StringComparison.Ordinal));
        Assert.Empty((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        // A matching late authoritative report still settles the attempt.
        Assert.Equal(WorkReportVerdict.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: task.Id)));
        var completed = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(completed.CurrentStage().Tasks).Status);
    }
}
