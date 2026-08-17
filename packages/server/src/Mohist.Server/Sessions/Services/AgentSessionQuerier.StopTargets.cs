using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public partial class AgentSessionQuerier
{
    public async Task<CanonicalTurnStopTarget?> ResolveCanonicalTurnStopTargetAsync(
        string projectId,
        string turnId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(turnId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.AgentSessions.AsNoTracking()
            .Where(row => row.LabelProjectId == projectId && row.State.Contains(turnId))
            .ToListAsync(ct);

        foreach (var row in candidates)
        {
            var session = AgentSessionJson.Deserialize(row);
            var turn = session?.Status.Turns?.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
            if (session is null || turn is null)
                continue;

            var metadata = session.Metadata;
            return new CanonicalTurnStopTarget(
                projectId,
                session.Id,
                turn.Id,
                turn.Sequence,
                session.BindingEpoch,
                new SessionStopTarget(
                    session.Runtime.RunnerId ?? string.Empty,
                    session.Id,
                    metadata.Label(AgentSessionQueryMetadataKeys.SourceKind) ?? string.Empty,
                    metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId),
                    metadata.Label(AgentSessionQueryMetadataKeys.SessionName),
                    session.Runtime.Runtime,
                    session.Status.AgentRuntimeSessionId,
                    session.Runtime.WorkDir));
        }

        return null;
    }
}

public sealed record CanonicalTurnStopTarget(
    string ProjectId,
    string SessionId,
    string TurnId,
    long TurnRevision,
    long ContextGeneration,
    SessionStopTarget DeliveryTarget);
