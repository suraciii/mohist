using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Grains;

public sealed class AgentJobRecoveryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MatchesBinding_RequiresTheCompletePhysicalExecutionIdentity()
    {
        var state = RunningState();
        var receipt = Receipt();

        Assert.True(AgentJobRecoveryPolicy.MatchesBinding(state, receipt));
        Assert.False(AgentJobRecoveryPolicy.MatchesBinding(state, receipt with { WorkId = "work-stale" }));
        Assert.False(AgentJobRecoveryPolicy.MatchesBinding(state, receipt with { RuntimeSessionId = "runtime-stale" }));
        Assert.False(AgentJobRecoveryPolicy.MatchesBinding(state, receipt with { AgentTurnId = "turn-stale" }));
    }

    [Theory]
    [InlineData(AgentLaunchVisibility.Visible, "agent-1", "prompt", true)]
    [InlineData(AgentLaunchVisibility.Rejected, "agent-1", "prompt", false)]
    [InlineData(AgentLaunchVisibility.Visible, null, "prompt", false)]
    [InlineData(AgentLaunchVisibility.Visible, "agent-1", "", false)]
    public void CanContinue_RequiresVisibleReplayableAgentInput(
        AgentLaunchVisibility visibility,
        string? agentId,
        string prompt,
        bool expected)
    {
        var state = RunningState();
        state.LaunchVisibility = visibility;
        state.Input = state.Input! with { AgentId = agentId, Prompt = prompt };

        Assert.Equal(expected, AgentJobRecoveryPolicy.CanContinue(state));
    }

    [Fact]
    public void Deadline_IsExceededOnlyForRecoverablyInterruptedWorkAtOrAfterTheDeadline()
    {
        var state = RunningState();
        state.Status = AgentJobStatus.RecoverablyInterrupted;
        state.UpdateInterruptionDeadlineAt = Now;

        Assert.False(AgentJobRecoveryPolicy.IsUpdateInterruptionDeadlineExceeded(state, Now.AddTicks(-1)));
        Assert.True(AgentJobRecoveryPolicy.IsUpdateInterruptionDeadlineExceeded(state, Now));

        state.Status = AgentJobStatus.Running;
        Assert.False(AgentJobRecoveryPolicy.IsUpdateInterruptionDeadlineExceeded(state, Now.AddHours(1)));
    }

    [Fact]
    public void RecordStopFailure_UpdatesTheMatchingInterruptionAndItsProjection()
    {
        var state = InterruptedState();

        var transition = AgentJobRecoveryPolicy.RecordStopFailure(
            state,
            "runner-1",
            "work-1",
            "update-1",
            "session.abort fetch failed",
            Now);

        Assert.NotNull(transition);
        Assert.Equal("session.abort fetch failed", state.Interruption!.StopFailure);
        Assert.Equal(Now, state.Interruption.RecordedAt);
        Assert.Contains("could not confirm", Assert.Single(state.InterruptionHistory).StopFailure);
    }

    [Fact]
    public void RecordStopFailure_RejectsAStaleFenceWithoutMutation()
    {
        var state = InterruptedState();
        var before = state.Interruption;

        var transition = AgentJobRecoveryPolicy.RecordStopFailure(
            state,
            "runner-1",
            "work-stale",
            "update-1",
            "failure",
            Now);

        Assert.Null(transition);
        Assert.Equal(before, state.Interruption);
        Assert.Empty(state.InterruptionHistory);
    }

    [Fact]
    public void EnterTerminal_PreservesExecutionIdentityAndClearsTheArbitrationDeadline()
    {
        var state = InterruptedState();
        state.ConcurrencyPermitId = "permit-1";

        AgentJobRecoveryPolicy.EnterTerminal(state, "agent-result-unconfirmed", Now);

        Assert.Equal(AgentJobStatus.Interrupted, state.Status);
        Assert.Equal("runner-1", state.RunnerId);
        Assert.Equal("work-1", state.WorkId);
        Assert.Equal("agent-result-unconfirmed", state.TerminalResult!.Message);
        Assert.Null(state.TerminalResult.FailureReason);
        Assert.Null(state.UpdateInterruptionDeadlineAt);
        Assert.Equal(AgentConcurrencyPermitStatus.Terminal, state.ConcurrencyGateStatus);
        Assert.True(state.ConcurrencyReleasePending);
    }

    [Fact]
    public void TerminalFingerprint_UsesTheRunnerCanonicalPayload()
    {
        var result = new WorkResult(
            "failed",
            "runtime failed",
            ExitCode: 1,
            Error: new ExecutionError("turn-failed", "runtime failed"));

        Assert.Equal(
            "a18211e2c1e34c9fb72e67cc409ff8aca2e4f6122a37655a3456ce14cb33747b",
            RuntimeRecoveryReceiptFingerprint.For(result));
    }

    private static AgentJobState RunningState() => new()
    {
        Status = AgentJobStatus.Running,
        RunnerId = "runner-1",
        WorkId = "work-1",
        RuntimeSessionId = "runtime-1",
        Input = new AgentJobInput(
            "prompt",
            Runtime: "opencode",
            AgentId: "agent-1",
            AgentSessionId: "session-1",
            InitialTurnId: "turn-1"),
    };

    private static AgentJobState InterruptedState()
    {
        var state = RunningState();
        state.Status = AgentJobStatus.RecoverablyInterrupted;
        state.UpdateOperationId = "update-1";
        state.UpdateInterruptionDeadlineAt = Now.AddMinutes(5);
        state.Interruption = new AgentWorkInterruptionTransition(
            AgentWorkInterruptionStates.Interrupted,
            "update-1",
            "work-1",
            null,
            0,
            "turn-1",
            null,
            null,
            "replacement",
            Now.AddMinutes(-1));
        return state;
    }

    private static RuntimeRecoveryReceipt Receipt() => new(
        WorkflowRunId: string.Empty,
        TaskRunId: string.Empty,
        WorkId: "work-1",
        RunnerId: "runner-1",
        AgentSessionId: "session-1",
        AgentTurnId: "turn-1",
        Runtime: "opencode",
        RuntimeSessionId: "runtime-1",
        RecoveryGeneration: 0,
        ReceiptId: "receipt-1",
        Payload: new RuntimeRecoveryReceiptPayload(
            RuntimeRecoveryReceiptPayloadTypes.UpdateInterrupted,
            UpdateOperationId: "update-1",
            StopConfirmed: true),
        OwnerKind: RuntimeRecoveryReceiptOwnerKinds.AgentJob,
        AgentJobId: "job-1");
}
