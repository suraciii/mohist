using Mohist.Server.Api;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

/// <summary>
/// Blocked Agent settlements are nonterminal attention: the wire status is
/// derived from the settlement at projection time, the persisted run status
/// stays untouched, and the only offered control is an explicit stop.
/// </summary>
public class WorkflowBlockedStatusProjectionTests
{
    [Fact]
    public void BlockedSettlement_DerivesBlockedWireStatusForRunStageAndTask()
    {
        var run = CreateRunWithSettlement(AgentResultSettlementState.Blocked);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.Equal("blocked", view!.Status);
        Assert.Equal("blocked", view.Stages[0].Status);
        Assert.Equal("blocked", view.Stages[0].Tasks[0].Status);
        Assert.Equal("pending", view.Stages[0].Tasks[1].Status);
    }

    [Fact]
    public void BlockedSettlement_ExposesStopActionAndAttentionWithoutFailure()
    {
        var run = CreateRunWithSettlement(AgentResultSettlementState.Blocked);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.Null(view!.Failure);
        Assert.Contains(view.AvailableActions, a => a.Name == "stop");
        Assert.DoesNotContain(view.AvailableActions, a => a.Name is "retry" or "rerun");
        Assert.True(WorkflowControlGuard.IsWorkflowControllableForAction("blocked", WorkflowControlAction.Stop));
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction("blocked", WorkflowControlAction.RetryOrRerun));
        Assert.False(WorkflowControlGuard.IsWorkflowControllableForAction("blocked", WorkflowControlAction.ActiveOnly));
        var attention = view.AgentResultAttention;
        Assert.NotNull(attention);
        Assert.Equal("blocked", attention!.State);
        Assert.Equal("agent-result-unconfirmed", attention.Reason);
        Assert.Equal("Runner disconnected before the Agent result was accepted.", attention.Message);
        Assert.Equal(TestDeadline, attention.DeadlineAt);
        Assert.Equal("proposal.1", attention.TaskRunId);
        Assert.Equal("proposal.1", attention.WorkId);
        Assert.Equal("runner-pluto", attention.RunnerId);
        Assert.Equal("agent-session-1", attention.AgentSessionId);
        Assert.Equal("turn-1", attention.AgentTurnId);
        Assert.Equal(WorkflowStatusMapper.AgentResultSettlementNextAction, attention.NextAction);
        Assert.Equal(["stop"], attention.RecoveryActions);

        var taskSettlement = view.Stages[0].Tasks[0].AgentResultSettlement;
        Assert.NotNull(taskSettlement);
        Assert.Equal("blocked", taskSettlement!.State);
        Assert.Equal("agent-result-unconfirmed", taskSettlement.Reason);
        Assert.Equal(TestDeadline, taskSettlement.DeadlineAt);
        Assert.Equal("agent-session-1", taskSettlement.AgentSessionId);
        Assert.Equal("turn-1", taskSettlement.AgentTurnId);
    }

    [Fact]
    public void UnknownSettlement_KeepsRunningWireStatusWithoutAttention()
    {
        var run = CreateRunWithSettlement(AgentResultSettlementState.Unknown);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.Equal("running", view!.Status);
        Assert.Equal("running", view.Stages[0].Status);
        Assert.Equal("running", view.Stages[0].Tasks[0].Status);
        Assert.Null(view.AgentResultAttention);
        var settlement = view.Stages[0].Tasks[0].AgentResultSettlement;
        Assert.NotNull(settlement);
        Assert.Equal("unknown", settlement!.State);
        Assert.Equal("runner-disconnected", settlement.Reason);
        Assert.Equal(TestDeadline, settlement.DeadlineAt);
        Assert.DoesNotContain(view.AvailableActions, a => a.Name == "stop");
    }

    [Fact]
    public void BlockedSettlementOnTerminalTask_IsNotProjectedAsBlocked()
    {
        var run = CreateRunWithSettlement(AgentResultSettlementState.Blocked, TaskRunStatus.Failed);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.Equal("running", view!.Status);
        Assert.Equal("failed", view.Stages[0].Tasks[0].Status);
        Assert.Null(view.AgentResultAttention);
    }

    [Fact]
    public void AwaitingResultSettlement_KeepsRunningWireStatus()
    {
        var run = CreateRunWithSettlement(AgentResultSettlementState.AwaitingResult);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.Equal("running", view!.Status);
        Assert.Null(view.AgentResultAttention);
    }

    [Fact]
    public void BlockedAgentResult_ProjectsToIssueBlockedWithStopActionOnly()
    {
        var workflow = new WorkflowStatusView(
            WorkflowRunId: "wr_1",
            Status: "blocked",
            CurrentStage: "plan",
            Stages: [],
            PendingWork: null,
            Failure: null,
            AvailableActions: [new AvailableActionView("stop", "Stop workflow", null)],
            AssignedTo: "runner-pluto",
            Metadata: null,
            AgentResultAttention: new AgentResultAttentionView(
                State: "blocked",
                Reason: "agent-result-unconfirmed",
                Message: "Agent result unconfirmed",
                DeadlineAt: TestDeadline,
                TaskRunId: "proposal.1",
                WorkId: "proposal.1",
                RunnerId: "runner-pluto"));

        var projection = MohistDefaultWorkflowProjection.ProjectWorkflowState(592, "Title", IssueStatus.InProgress, workflow);

        Assert.Equal("blocked", projection.Health);
        Assert.Equal("Agent result unconfirmed", projection.BlockedReason);
        var attention = projection.Attention;
        Assert.NotNull(attention);
        Assert.Equal(WorkflowAttentionReason.AgentResultUnconfirmed, attention!.Reason);
        Assert.Equal(["stop"], attention.AvailableActions);
    }

    private static readonly DateTimeOffset TestDeadline = new(2026, 8, 14, 11, 1, 58, TimeSpan.Zero);

    private static WorkflowRun CreateRunWithSettlement(
        AgentResultSettlementState state,
        TaskRunStatus taskStatus = TaskRunStatus.Running)
    {
        return new WorkflowRun
        {
            Id = "wf-blocked",
            Metadata = new WorkflowRunMetadata("test", TestTime.UtcNow),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "plan",
            Stages =
            [
                new StageRun
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Tasks =
                    [
                        new TaskRun
                        {
                            Id = "proposal.1",
                            DefinitionId = "proposal",
                            Attempt = 1,
                            Title = "Generate proposal",
                            Status = taskStatus,
                            Uses = "mohist/pi",
                            AgentResultSettlement = new AgentResultSettlement
                            {
                                State = state,
                                TaskRunId = "proposal.1",
                                WorkId = "proposal.1",
                                RunnerId = "runner-pluto",
                                AgentSessionId = "agent-session-1",
                                AgentTurnId = "turn-1",
                                Runtime = "pi",
                                LastObservation = AgentExecutionObservationKind.Disconnected,
                                ReasonCode = "runner-disconnected",
                                Message = "Runner disconnected before the Agent result was accepted.",
                                DeadlineAt = TestDeadline,
                            },
                        },
                        new TaskRun
                        {
                            Id = "specs.1",
                            DefinitionId = "specs",
                            Attempt = 1,
                            Title = "Write specs",
                            Status = TaskRunStatus.Pending,
                            Uses = "mohist/pi",
                        },
                    ],
                },
            ],
        };
    }
}
