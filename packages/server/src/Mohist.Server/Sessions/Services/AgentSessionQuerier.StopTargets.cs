using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public partial class AgentSessionQuerier
{
    public async Task<CanonicalFollowupTarget?> ResolveCanonicalFollowupTargetAsync(
        string projectId,
        string sessionId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var records = await _sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null || !string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal))
            return null;

        var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind);
        var workflowRunId = record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
        var sessionName = record.Label(AgentSessionQueryMetadataKeys.SessionName);
        if (string.Equals(sourceKind, "workflow", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(sessionName))
                return null;
        }
        else if (!string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal)
            && !string.Equals(sourceKind, "agent-connection", StringComparison.Ordinal))
        {
            return null;
        }

        var session = record.Session;
        return new CanonicalFollowupTarget(
            session.Runtime.RunnerId,
            session.Id,
            sourceKind!,
            workflowRunId,
            sessionName,
            session.Runtime.Runtime,
            session.Status.AgentRuntimeSessionId,
            session.Runtime.WorkDir,
            string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal)
                ? session.Settings.Definition
                : null,
            record.Label(AgentSessionQueryMetadataKeys.ProjectId),
            record.Label(GenericAgentSessionMetadata.AgentId),
            record.Label(AgentSessionQueryMetadataKeys.ConnectionId));
    }

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
