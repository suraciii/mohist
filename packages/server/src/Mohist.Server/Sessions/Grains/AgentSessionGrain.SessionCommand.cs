using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private static SessionCommandRequest BuildSessionCommandRequest(
        AgentSession session,
        SessionCommandKind command,
        AgentSessionResetReservation reservation) =>
        new(
            SessionId: session.Id,
            Runtime: reservation.Runtime,
            RuntimeSessionId: session.Status.AgentRuntimeSessionId,
            RunnerId: session.Runtime.RunnerId,
            WorkDir: session.Runtime.WorkDir,
            Command: command,
            ExpectedRuntimeSessionId: command == SessionCommandKind.Reset ? reservation.ExpectedRuntimeSessionId : null,
            OperationId: reservation.OperationId,
            ProjectId: session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId),
            ProcessGeneration: reservation.OwnerProcessGeneration!);

    private static SessionCommandRequest BuildSessionCommandRequest(
        AgentSession session,
        SessionCommandKind command,
        AgentSessionCommandAdmissionTombstone admission) =>
        new(
            SessionId: session.Id,
            Runtime: session.Runtime.Runtime!,
            RuntimeSessionId: session.Status.AgentRuntimeSessionId,
            RunnerId: session.Runtime.RunnerId,
            WorkDir: session.Runtime.WorkDir,
            Command: command,
            ExpectedRuntimeSessionId: null,
            OperationId: admission.OperationId,
            ProjectId: session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId),
            ProcessGeneration: admission.OwnerProcessGeneration);

    private static AgentSessionRecoveryOutcome ToRecoveryOutcome(AgentSessionRecoveryResult result) => new(
        result.Id,
        result.Status,
        result.ContextWindowSize,
        result.ContextWindowUsed,
        result.ContextUsagePercent,
        result.ContextWindowUsedBefore,
        result.Operation,
        result.WasCompacted);

    private static AgentSessionRecoveryResult ToRecoveryResult(AgentSessionRecoveryOutcome outcome) => new(
        outcome.Id,
        outcome.Status,
        outcome.ContextWindowSize,
        outcome.ContextWindowUsed,
        outcome.ContextUsagePercent,
        outcome.ContextWindowUsedBefore,
        outcome.Operation,
        outcome.WasCompacted);

    private static string RecoveryIdempotencyKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;

    private static bool MatchesRecoveryIdempotencyKey(AgentSessionResetReservation reservation, string key) =>
        string.Equals(reservation.IdempotencyKey, key, StringComparison.Ordinal);

    private static IReadOnlyList<AgentSessionCommandAdmissionTombstone> CompleteSessionCommandAdmission(
        AgentSession session,
        string operationId,
        AgentSessionRecoveryOutcome outcome) =>
        (session.Status.SessionCommandAdmissionFacts ?? [])
            .Select(candidate => string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal)
                ? candidate with { Outcome = outcome }
                : candidate)
            .ToArray();

    private static AgentSessionResetReservation RequireReservation(
        AgentSession session,
        string operationId,
        SessionCommandKind command,
        string ownerProcessGeneration)
    {
        var reservation = session.Status.PendingReset;
        if (reservation is null
            || !string.Equals(reservation.OperationId, operationId, StringComparison.Ordinal)
            || !string.Equals(reservation.OwnerProcessGeneration, ownerProcessGeneration, StringComparison.Ordinal))
            throw new StaleRuntimeSessionBindingException(session.Id, operationId, reservation?.OperationId);
        var commandName = CommandName(command);
        if (!string.Equals(reservation.Command, commandName, StringComparison.Ordinal))
            throw new RecoveryOperationInProgressException(session.Id, reservation.Command);
        var admission = session.Status.SessionCommandAdmissionFacts?.LastOrDefault(candidate =>
            string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
        if (!reservation.EffectAdmitted
            || admission is null
            || !string.Equals(admission.Command, commandName, StringComparison.Ordinal)
            || !string.Equals(admission.IdempotencyKey, reservation.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(admission.OwnerProcessGeneration, ownerProcessGeneration, StringComparison.Ordinal))
            throw new StaleRuntimeSessionBindingException(session.Id, operationId, admission?.OperationId);
        return reservation;
    }

    private static string CommandName(SessionCommandKind command) => command switch
    {
        SessionCommandKind.Compact => "compact",
        SessionCommandKind.Reset => "reset",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported session command"),
    };
}
