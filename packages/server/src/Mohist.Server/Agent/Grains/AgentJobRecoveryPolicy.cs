using Mohist.Server.Contracts;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Agent.Grains;

internal static class AgentJobRecoveryPolicy
{
    internal static bool MatchesBinding(AgentJobState state, RuntimeRecoveryReceipt receipt)
    {
        var input = state.Input;
        var expectedRuntime = input?.Runtime ?? AgentConfigSchema.OpenCodeRuntime;
        return string.Equals(receipt.RunnerId, state.RunnerId, StringComparison.Ordinal)
            && string.Equals(receipt.WorkId, state.WorkId, StringComparison.Ordinal)
            && string.Equals(receipt.AgentSessionId, input?.AgentSessionId, StringComparison.Ordinal)
            && string.Equals(receipt.AgentTurnId, input?.InitialTurnId, StringComparison.Ordinal)
            && string.Equals(receipt.Runtime, expectedRuntime, StringComparison.Ordinal)
            && string.Equals(receipt.RuntimeSessionId, state.RuntimeSessionId, StringComparison.Ordinal);
    }

    internal static bool CanContinue(AgentJobState state) =>
        state.LaunchVisibility == AgentLaunchVisibility.Visible
        && state.Input is { } input
        && !string.IsNullOrWhiteSpace(input.AgentId)
        && (!string.IsNullOrWhiteSpace(input.Prompt)
            || input.Attachments is { Count: > 0 });

    internal static bool IsUpdateInterruptionDeadlineExceeded(
        AgentJobState state,
        DateTimeOffset now) =>
        state.Status == AgentJobStatus.RecoverablyInterrupted
        && state.UpdateInterruptionDeadlineAt is { } deadline
        && deadline <= now;

    internal static AgentWorkInterruptionTransition? RecordStopFailure(
        AgentJobState state,
        string runnerId,
        string workId,
        string updateOperationId,
        string failure,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(failure)
            || state.Status != AgentJobStatus.RecoverablyInterrupted
            || !string.Equals(state.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(state.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(state.UpdateOperationId, updateOperationId, StringComparison.Ordinal)
            || state.Interruption is null)
        {
            return null;
        }

        var transition = state.Interruption with { StopFailure = failure, RecordedAt = now };
        state.Interruption = transition;
        state.InterruptionHistory = AgentWorkInterruptionProjection.Apply(
            state.InterruptionHistory,
            transition).ToList();
        return transition;
    }

    internal static AgentJobTerminalResult EnterTerminal(
        AgentJobState state,
        string reason,
        DateTimeOffset now)
    {
        state.Status = AgentJobStatus.Interrupted;
        state.RecoveryTerminalReason = reason;
        state.UpdateInterruptionDeadlineAt = null;
        state.FailureReason = null;
        state.RunningSince = null;
        state.TerminalAt = now;
        var result = new AgentJobTerminalResult(
            AgentJobStatus.Interrupted,
            reason,
            null,
            null,
            null,
            null);
        state.TerminalResult = result;
        state.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.Terminal;
        state.ConcurrencyReleasePending = state.ConcurrencyPermitId is not null
            || state.ConcurrencyPermitHeld
            || state.ConcurrencyWaiterId is not null;
        return result;
    }
}
