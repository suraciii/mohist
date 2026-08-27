using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<SessionCommandAdmissionOutcome> AdmitSessionCommandEffectAsync(
        string operationId,
        string ownerProcessGeneration)
    {
        var session = await GetRequiredAsync();
        var reservation = session.Status.PendingReset;
        if (reservation is null
            || !string.Equals(reservation.OperationId, operationId, StringComparison.Ordinal)
            || !string.Equals(reservation.OwnerProcessGeneration, ownerProcessGeneration, StringComparison.Ordinal))
            return SessionCommandAdmissionOutcome.Missing;

        if (reservation.Outcome is not null || HasSessionCommandAdmission(session, operationId))
            return SessionCommandAdmissionOutcome.AlreadyAdmitted;

        var admitted = reservation with { EffectAdmitted = true };
        var fact = new AgentSessionCommandAdmissionTombstone(
            admitted.Command,
            admitted.OperationId,
            admitted.IdempotencyKey!,
            ownerProcessGeneration);
        session.Status = session.Status with
        {
            PendingReset = admitted,
            SessionCommandAdmissionFacts = (session.Status.SessionCommandAdmissionFacts ?? [])
                .Append(fact)
                .ToArray(),
        };
        await CommitAsync(session, []);
        return SessionCommandAdmissionOutcome.AdmittedNow;
    }

    private static bool HasSessionCommandAdmission(AgentSession session, string operationId) =>
        session.Status.SessionCommandAdmissionFacts?.Any(candidate =>
            string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal)) == true;
}
