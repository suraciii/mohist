using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Runner.Domain;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public sealed class AgentResultSettlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SettlementTimeout = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData("mohist/agent")]
    [InlineData("mohist/opencode")]
    [InlineData("mohist/pi")]
    public void StartTask_CreatesSettlementForWorkflowAgentExecutors(string uses)
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", uses));

        var task = StartTask(run, "agent-work");

        var settlement = Assert.IsType<AgentResultSettlement>(task.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.AwaitingResult, settlement.State);
        Assert.Equal(task.Id, settlement.TaskRunId);
        Assert.Equal("agent-work", settlement.WorkId);
        Assert.Equal("runner-1", settlement.RunnerId);
        Assert.False(run.HasUnresolvedAgentResult());
    }

    [Fact]
    public void StartTask_DoesNotCreateSettlementForNonAgentTasksOrChecks()
    {
        var run = BuildRun(
            new TaskDefinition("task", "Task", "spec/task"),
            new TaskDefinition("agent", "Agent", "mohist/agent"));

        var nonAgent = StartTask(run, "task-work");

        Assert.Null(nonAgent.AgentResultSettlement);
        Assert.False(run.HasUnresolvedAgentResult());
        Assert.Empty(run.CurrentStage().Checks);
    }

    [Fact]
    public void BindAgentExecution_AcceptsExactReplayAndRejectsAnyIdentityMismatch()
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/opencode"));
        var task = StartTask(run, "agent-work");
        var binding = Binding(task);

        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(binding));
        var before = JSON.Serialize(task.AgentResultSettlement);

        Assert.Equal(AgentExecutionUpdate.Unchanged, run.BindAgentExecution(binding));
        foreach (var mismatch in new[]
                 {
                     binding with { AgentSessionId = "other-session" },
                     binding with { AgentTurnId = "other-turn" },
                     binding with { Runtime = "pi" },
                     binding with { RuntimeSessionId = "other-runtime-session" },
                     binding with { TaskRunId = "other-task" },
                     binding with { WorkId = "other-work" },
                     binding with { RunnerId = "other-runner" }
                 })
        {
            Assert.Equal(AgentExecutionUpdate.Rejected, run.BindAgentExecution(mismatch));
        }

        Assert.Equal(before, JSON.Serialize(task.AgentResultSettlement));
    }

    [Theory]
    [InlineData(AgentExecutionObservationKind.Idle)]
    [InlineData(AgentExecutionObservationKind.Completed)]
    [InlineData(AgentExecutionObservationKind.Stopped)]
    [InlineData(AgentExecutionObservationKind.StopUnconfirmed)]
    [InlineData(AgentExecutionObservationKind.TargetMissing)]
    [InlineData(AgentExecutionObservationKind.Disconnected)]
    public void ObserveAgentExecution_RecordsPhysicalFactsWithoutFailingTheTask(AgentExecutionObservationKind kind)
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/pi"));
        var task = StartTask(run, "agent-work");
        var binding = Binding(task);
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(binding));

        var observation = new AgentExecutionObservation(binding, kind, "physical-fact", "physical detail", "stop-1");

        Assert.Equal(AgentExecutionUpdate.Updated, run.ObserveAgentExecution(observation, Now, SettlementTimeout));

        var settlement = Assert.IsType<AgentResultSettlement>(task.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(kind, settlement.LastObservation);
        Assert.Equal("physical-fact", settlement.ReasonCode);
        Assert.Equal(Now, settlement.FirstUnknownAt);
        Assert.Equal(Now + SettlementTimeout, settlement.DeadlineAt);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Null(run.Failure);
        Assert.Null(run.CurrentStage().Failure);
        Assert.True(run.HasUnresolvedAgentResult());
    }

    [Fact]
    public void ObserveAgentExecution_IsIdempotentAndCannotOverwriteBoundExecution()
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/agent"));
        var task = StartTask(run, "agent-work");
        var binding = Binding(task);
        var observation = new AgentExecutionObservation(binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed", StopOperationId: "stop-1");
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(binding));
        Assert.Equal(AgentExecutionUpdate.Updated, run.ObserveAgentExecution(observation, Now, SettlementTimeout));
        var before = JSON.Serialize(task.AgentResultSettlement);

        Assert.Equal(AgentExecutionUpdate.Unchanged, run.ObserveAgentExecution(observation, Now.AddMinutes(1), SettlementTimeout));
        Assert.Equal(AgentExecutionUpdate.Unchanged, run.ObserveAgentExecution(
            observation with { StopOperationId = null }, Now.AddMinutes(1), SettlementTimeout));
        Assert.Equal(AgentExecutionUpdate.Rejected, run.ObserveAgentExecution(
            observation with { Binding = binding with { AgentTurnId = "reused-session-new-turn" } }, Now, SettlementTimeout));

        Assert.Equal(before, JSON.Serialize(task.AgentResultSettlement));
    }

    [Fact]
    public void ReportableAttempt_RequiresThePersistedTaskRunWorkAndRunnerAcrossUnresolvedStates()
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/agent"));
        var task = StartTask(run, "agent-work");
        var binding = Binding(task);
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(binding));

        AssertReportable(run, task);
        Assert.Equal(AgentExecutionUpdate.Updated, run.ObserveAgentExecution(
            new AgentExecutionObservation(binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected"), Now, SettlementTimeout));
        AssertReportable(run, task);

        task.AgentResultSettlement!.State = AgentResultSettlementState.Blocked;
        AssertReportable(run, task);
        Assert.Null(run.FindReportableTaskAttempt("other-task", task.WorkId!, task.WorkerId!));
        Assert.Null(run.FindReportableTaskAttempt(task.Id, "other-work", task.WorkerId!));
        Assert.Null(run.FindReportableTaskAttempt(task.Id, task.WorkId!, "other-runner"));

        run.FailTask(new TaskResult("failed", "authoritative failure"), Now);

        Assert.Null(run.FindReportableTaskAttempt(task.Id, "agent-work", "runner-1"));
        Assert.False(run.HasUnresolvedAgentResult());
    }

    [Fact]
    public void ReportShape_UsesUniqueTaskRunAndWorkIdentityAcrossStages()
    {
        var first = new TaskDefinition("repeat", "Plan repeat", "spec/task");
        var second = new TaskDefinition("repeat", "Build repeat", "spec/task");
        var run = WorkflowRun.Create(
            "wr-report-shape",
            new WorkflowDefinition([
                new StageDefinition("plan", [first], []),
                new StageDefinition("build", [second], [])
            ]),
            Now);
        run.Start(Now);
        run.InitializeStage([first], [], Now);
        var firstTask = Assert.Single(run.CurrentStage().Tasks);
        run.AssignTo("runner-1", Now);
        run.StartTask("repeat-plan", "runner-1", Now);
        run.CompleteTask(Now);
        run.InitializeStage([second], [], Now);
        var secondTask = Assert.Single(run.CurrentStage().Tasks);
        run.StartTask("repeat-build", "runner-1", Now);

        var shape = Assert.IsType<WorkItem>(run.FindReportShape(secondTask.Id, "repeat-build"));

        Assert.NotEqual(firstTask.Id, secondTask.Id);
        Assert.Equal("repeat.1", firstTask.Id);
        Assert.Equal("repeat.1.run2", secondTask.Id);
        Assert.Equal("build", shape.Stage);
        Assert.Equal("Build repeat", shape.Title);
        Assert.True(shape.IsTask);
        Assert.Null(run.FindReportShape(secondTask.Id, "other-work"));
    }

    [Fact]
    public void ReusedSessionOldObservationIsStaleAfterTheOriginalTaskSettles()
    {
        var run = BuildRun(
            new TaskDefinition("first", "First", "mohist/agent"),
            new TaskDefinition("second", "Second", "mohist/agent"));
        var first = StartTask(run, "first-work");
        var firstBinding = Binding(first, sessionId: "reused-session", turnId: "old-turn");
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(firstBinding));
        run.CompleteTask(Now);

        var second = StartTask(run, "second-work");
        var secondBinding = Binding(second, sessionId: "reused-session", turnId: "new-turn");
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(secondBinding));

        Assert.Equal(AgentExecutionUpdate.Rejected, run.ObserveAgentExecution(
            new AgentExecutionObservation(firstBinding, AgentExecutionObservationKind.Idle, "old-turn-idle"), Now, SettlementTimeout));
        Assert.Equal(AgentResultSettlementState.AwaitingResult, second.AgentResultSettlement!.State);
        Assert.Equal(TaskRunStatus.Completed, first.Status);
        Assert.Equal(TaskRunStatus.Running, second.Status);
    }

    [Fact]
    public void ConclusiveNonAgentFailureRetainsExistingFailureBehavior()
    {
        var run = BuildRun(new TaskDefinition("task", "Task", "spec/task"));
        var task = StartTask(run, "task-work");

        var events = run.FailTask(new TaskResult("failed", "broken"), Now);

        Assert.Null(task.AgentResultSettlement);
        Assert.False(run.HasUnresolvedAgentResult());
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Collection(
            events,
            @event => Assert.IsType<TaskFailed>(WorkflowEventSerializer.Unwrap(@event)),
            @event => Assert.IsType<StageFailed>(WorkflowEventSerializer.Unwrap(@event)),
            @event => Assert.IsType<WorkflowRunFailed>(WorkflowEventSerializer.Unwrap(@event)));
    }

    [Fact]
    public void UnknownSettlement_UsesOneFixedDeadlineAndBlocksWithoutFailure()
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/agent"));
        var task = StartTask(run, "agent-work");
        var binding = Binding(task);
        var observation = new AgentExecutionObservation(binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed");
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(binding));
        Assert.Equal(AgentExecutionUpdate.Updated, run.ObserveAgentExecution(observation, Now, SettlementTimeout));

        var deadline = task.AgentResultSettlement!.DeadlineAt;
        Assert.Equal(AgentExecutionUpdate.Updated, run.ObserveAgentExecution(
            observation with { Message = "replayed after reconnect" },
            Now.AddMinutes(4),
            SettlementTimeout));
        Assert.Equal(deadline, task.AgentResultSettlement.DeadlineAt);
        Assert.Empty(run.BlockUnresolvedAgentResult(Now.AddMinutes(4).AddSeconds(59)));

        var events = run.BlockUnresolvedAgentResult(deadline!.Value);

        Assert.Equal(AgentResultSettlementState.Blocked, task.AgentResultSettlement.State);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Null(run.Failure);
        Assert.Null(run.CurrentStage().Failure);
        Assert.Collection(
            events,
            @event => Assert.IsType<TaskBlocked>(WorkflowEventSerializer.Unwrap(@event)),
            @event => Assert.IsType<StageBlocked>(WorkflowEventSerializer.Unwrap(@event)),
            @event => Assert.IsType<WorkflowRunBlocked>(WorkflowEventSerializer.Unwrap(@event)));
    }

    [Fact]
    public void BlockedEvents_HaveDistinctCatalogTypesAndWorkflowLineage()
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/agent"));
        run.Metadata = new WorkflowRunMetadata(null, Now, ProjectId: "proj_event", IssueNumber: 589);
        var task = StartTask(run, "agent-work");
        task.AgentResultSettlement!.State = AgentResultSettlementState.Blocked;
        task.AgentResultSettlement.DeadlineAt = Now.Add(SettlementTimeout);

        WorkflowEvent[] events =
        [
            new AgentTaskResultUnconfirmed("build", task.Id, "agent-work", "stop-unconfirmed", Now.Add(SettlementTimeout)),
            new TaskBlocked("build", task.Id, "agent-result-unconfirmed", Now.Add(SettlementTimeout)),
            new StageBlocked("build", task.Id, "agent-result-unconfirmed"),
            new WorkflowRunBlocked("build", task.Id, "agent-result-unconfirmed", Now.Add(SettlementTimeout)),
        ];

        var types = events.Select(WorkflowEventSerializer.BusType).ToArray();
        Assert.Equal(types.Length, types.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [
                EventCatalog.ReverseDns.AgentTaskResultUnconfirmed,
                EventCatalog.ReverseDns.TaskBlocked,
                EventCatalog.ReverseDns.StageBlocked,
                EventCatalog.ReverseDns.WorkflowRunBlocked,
            ],
            types);
        Assert.All(events, @event =>
        {
            Assert.Contains(WorkflowEventSerializer.BusType(@event), EventCatalog.All);
            var lineage = WorkflowRunLineage.BuildExtensions(run, @event);
            Assert.Equal("build", lineage[EventCatalog.Lineage.Stage]);
            Assert.Equal(run.Id, lineage[EventCatalog.Lineage.WorkflowRunId]);
            var restored = WorkflowEventSerializer.FromData(
                WorkflowEventSerializer.Type(@event),
                WorkflowEventSerializer.ToData(@event));
            Assert.Equal(WorkflowEventSerializer.Type(@event), WorkflowEventSerializer.Type(restored));
        });
    }

    [Fact]
    public void ExplicitStop_DoesNotCancelAwaitingAgentResult()
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/opencode"));
        var task = StartTask(run, "agent-work");
        var binding = Binding(task);
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(binding));

        var cancellation = run.CancelUnresolvedAgentTaskForStop(Now.AddMinutes(1));

        Assert.Empty(cancellation);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(AgentResultSettlementState.AwaitingResult, task.AgentResultSettlement!.State);
    }

    [Fact]
    public void ExplicitStop_CancelsUnresolvedTaskWithoutDiscardingItsExecutionIdentity()
    {
        var run = BuildRun(new TaskDefinition("agent", "Agent", "mohist/pi"));
        var task = StartTask(run, "agent-work");
        var binding = Binding(task);
        Assert.Equal(AgentExecutionUpdate.Updated, run.BindAgentExecution(binding));
        Assert.Equal(AgentExecutionUpdate.Updated, run.ObserveAgentExecution(
            new AgentExecutionObservation(binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected"),
            Now,
            SettlementTimeout));

        var cancellation = run.CancelUnresolvedAgentTaskForStop(Now.AddMinutes(1));
        var stop = run.Stop();

        Assert.Equal(TaskRunStatus.Cancelled, task.Status);
        Assert.Equal(
            new TerminalLogOwnership(TerminalLogOwnerKinds.Workflow, run.Id, "agent-work", "runner-1"),
            task.TerminalLogOwnership);
        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.Equal("agent-work", task.WorkId);
        Assert.Equal("runner-1", task.WorkerId);
        Assert.NotNull(task.AgentResultSettlement);
        Assert.False(run.HasUnresolvedAgentResult());
        Assert.Null(run.FindReportableTaskAttempt(task.Id, task.WorkId!, task.WorkerId!));
        Assert.IsType<TaskCancelled>(WorkflowEventSerializer.Unwrap(Assert.Single(cancellation)));
        Assert.IsType<WorkflowRunStopped>(WorkflowEventSerializer.Unwrap(Assert.Single(stop)));
        Assert.Null(run.Failure);
        Assert.Null(run.CurrentStage().Failure);
    }

    private static WorkflowRun BuildRun(params TaskDefinition[] tasks)
    {
        var run = WorkflowRun.Create(
            "wr-settlement",
            new WorkflowDefinition([new StageDefinition("build", tasks.ToList(), [])]),
            Now);
        run.Start(Now);
        run.InitializeStage(tasks.ToList(), [], Now);
        run.AssignTo("runner-1", Now);
        return run;
    }

    private static TaskRun StartTask(WorkflowRun run, string workId)
    {
        run.StartTask(workId, "runner-1", Now);
        return Assert.IsType<TaskRun>(run.CurrentStage().RunningTask);
    }

    private static AgentExecutionBinding Binding(TaskRun task, string sessionId = "session-1", string turnId = "turn-1") =>
        new(task.Id, task.WorkId!, task.WorkerId!, sessionId, turnId, "opencode", "runtime-session-1");

    private static void AssertReportable(WorkflowRun run, TaskRun task)
    {
        var attempt = Assert.IsType<WorkflowReportableTaskAttempt>(
            run.FindReportableTaskAttempt(task.Id, task.WorkId!, task.WorkerId!));
        Assert.Equal(task.Id, attempt.TaskRunId);
        Assert.Equal(task.WorkId, attempt.WorkId);
        Assert.Equal(task.WorkerId, attempt.RunnerId);
        Assert.Equal(task.AgentResultSettlement?.State, attempt.SettlementState);
    }
}
