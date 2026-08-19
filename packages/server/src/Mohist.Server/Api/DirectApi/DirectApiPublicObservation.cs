using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.DirectApi;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// Serializes the one public observation that can exist without a live
/// canonical anchor: a durable pre-admission rejection. Accepted outcomes
/// originate from persisted projection snapshots before the idempotency
/// mapping freezes their replay body.
/// </summary>
public static class DirectApiPublicObservation
{
    public static string Rejected(
        string projectId,
        string agentId,
        DirectApiLaunchOutcome outcome,
        DateTimeOffset observedAt)
    {
        var reasonCode = outcome.RejectionCode ?? DirectApiErrorCodes.AgentNotReady;
        var publicError = new PublicExecutionError
        {
            Code = reasonCode,
            Message = reasonCode switch
            {
                DirectApiErrorCodes.AgentNotReady => "The requested Agent is not ready to accept work.",
                _ => "The launch was rejected before execution could begin.",
            },
        };
        var dto = new PublicExecutionRead
        {
            ProjectId = projectId,
            AgentId = agentId,
            JobId = outcome.JobId,
            SessionId = outcome.SessionId,
            InputId = outcome.InputId,
            TurnId = outcome.TurnId,
            Status = PublicExecutionFieldValues.StatusTerminal,
            JobStatus = outcome.JobId is null ? null : PublicExecutionFieldValues.JobTerminal,
            SessionActivity = null,
            Admission = null,
            InputStatus = null,
            TurnStatus = null,
            Outcome = PublicExecutionFieldValues.OutcomeRejected,
            ReasonCode = reasonCode,
            Output = null,
            Error = publicError,
            AcceptedAt = null,
            QueuedAt = null,
            StartedAt = null,
            TerminalAt = observedAt,
            ObservedAt = observedAt,
            Sequence = null,
        };
        return System.Text.Json.JsonSerializer.Serialize(dto, JSON.PublicApi);
    }

    public static string RejectedFollowup(
        string projectId,
        DirectApiFollowupOutcome outcome,
        DateTimeOffset observedAt)
    {
        var reasonCode = outcome.RejectionCode ?? DirectApiErrorCodes.FollowupRejected;
        var publicError = new PublicExecutionError
        {
            Code = reasonCode,
            Message = reasonCode == PublicExecutionFieldValues.Reasons.QueueFull
                ? "The follow-up was rejected because the execution queue is full."
                : "The follow-up was rejected before execution could begin.",
        };
        var dto = new PublicExecutionRead
        {
            ProjectId = projectId,
            AgentId = outcome.AgentId,
            JobId = null,
            SessionId = outcome.SessionId,
            InputId = null,
            TurnId = null,
            Status = PublicExecutionFieldValues.StatusTerminal,
            JobStatus = null,
            SessionActivity = null,
            Admission = null,
            InputStatus = null,
            TurnStatus = null,
            Outcome = PublicExecutionFieldValues.OutcomeRejected,
            ReasonCode = reasonCode,
            Output = null,
            Error = publicError,
            AcceptedAt = null,
            QueuedAt = null,
            StartedAt = null,
            TerminalAt = observedAt,
            ObservedAt = observedAt,
            Sequence = null,
        };
        return System.Text.Json.JsonSerializer.Serialize(dto, JSON.PublicApi);
    }
}
