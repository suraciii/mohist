using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Services;

public partial class AgentSessionQuerier
{
    /// <summary>
    /// Builds the generic-session summary surfaced by
    /// <c>GET /api/projects/{projectRef}/agent-sessions/{sessionId}</c>.
    /// Returns <c>null</c> when the
    /// session id does not resolve to a generic <c>agent-launch</c>
    /// session in the requested project — the cross-project guard
    /// matches <see cref="ResolveGenericFollowupTargetAsync"/> so the caller never
    /// observes a session from a different project.
    /// </summary>
    /// <remarks>
    /// The DTO omits workflow-only fields (workflowRunId, sessionName,
    /// workId, workType, stage) by construction — the record does not
    /// declare them. Activity surfaces the authoritative
    /// <c>idle</c> / <c>active</c> / <c>unknown</c> value resolved by the
    /// same logic that powers
    /// <see cref="ListAgentSessionsAsync"/> so list and summary stay in
    /// lockstep. Resolved model, failure category, and tool
    /// call/error counts are computed via
    /// <see cref="TranscriptEventSummaryProjector"/> over the session's
    /// transcript; the context-ref envelope is sourced from the
    /// <see cref="GenericAgentSessionMetadata"/> labels stamped at
    /// launch.
    /// </remarks>
    public async Task<GenericAgentSessionSummaryDto?> GetGenericSessionSummaryAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await FindGenericSessionAsync(projectId, sessionId, ct);
        if (record is null) return null;

        var session = record.Session;
        var loaded = await TranscriptPartLoader.LoadAsync(db, new[] { session.Id }, ct: ct);
        var transcriptEvents = ToTranscriptProjectionsInSequenceOrder(loaded);
        var turnSequenceByTurnId = loaded.Turns.ToDictionary(t => t.Id, t => t.Sequence);
        var runtimeSessionId = session.Status.AgentRuntimeSessionId;

        var summary = TranscriptEventSummaryProjector.Summarize(
            transcriptEvents
                .Where(e => IsApplicableToCurrentRuntime(e, turnSequenceByTurnId, runtimeSessionId, loaded))
                .Select(e => new TranscriptSummaryEvent(
                    TurnSequence: turnSequenceByTurnId.GetValueOrDefault(e.TurnId, 0),
                    Sequence: e.Sequence,
                    PartId: e.Id.ToString(),
                    Type: e.Type,
                    PayloadJson: e.PayloadJson)));

        var activity = ResolveAgentSessionActivity(record);
        var interruption = AgentSessionObservationMapper.Current(session.Status);

        var usage = AgentSessionJsonHelper.Usage(session);

        return new GenericAgentSessionSummaryDto(
            session.Id,
            record.Label(GenericAgentSessionMetadata.AgentId) ?? string.Empty,
            record.Label(GenericAgentSessionMetadata.AgentName) ?? string.Empty,
            session.Status.AgentRuntimeSessionId,
            session.Runtime.Runtime,
            activity,
            session.Status.CreatedAt.ToString("o"),
            AgentSessionJsonHelper.LastActivityAt(session).ToString("o"),
            summary.ResolvedModel,
            interruption is null ? summary.FailureCategory : null,
            interruption is null ? summary.FailureReason : null,
            summary.ToolCallCount,
            summary.ToolErrorCount,
            BuildGenericSessionSummaryContextRefs(record),
            AgentSessionDtoMapper.ToUsageDto(usage),
            session.Status.Activity == AgentSessionActivity.Idle,
            CurrentTurnId(session),
            AgentSessionObservationMapper.Inputs(session.Status),
            AgentSessionObservationMapper.Turns(session.Status),
            Interruption: interruption,
            InterruptionHistory: AgentSessionObservationMapper.History(session.Status),
            Origin: record.Label(GenericAgentSessionMetadata.Origin),
            TargetId: record.Label(GenericAgentSessionMetadata.TargetId),
            AppliedReasoningEffort: summary.AppliedReasoningEffort);
    }

    /// <summary>
    /// Builds the unified source-agnostic session summary surfaced by
    /// <c>GET /api/projects/{projectRef}/sessions/{sessionId}</c>.
    /// Resolves the row by id WITHOUT the
    /// source gate, so a workflow-originated or Agent Connection session's
    /// summary resolves here by the same stable id as an agent-launch
    /// session's. Returns <c>null</c> for an unknown id, a
    /// cross-project session, or an unknown source kind.
    /// </summary>
    public async Task<UnifiedSessionSummaryDto?> GetUnifiedSessionSummaryAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        var record = await FindUnifiedSessionAsync(projectId, sessionId, ct);
        if (record is null) return null;

        var session = record.Session;
        var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind);
        var isWorkflow = string.Equals(sourceKind, "workflow", StringComparison.Ordinal);
        var isAgentSession = string.Equals(sourceKind, AgentLaunchSourceKind, StringComparison.Ordinal)
            || string.Equals(sourceKind, AgentConnectionSourceKind, StringComparison.Ordinal);
        if (!isWorkflow && !isAgentSession) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var transcriptSummary = await ResolveTranscriptSummaryAsync(db, session.Id, session, ct);

        var activity = ResolveAgentSessionActivity(record);
        var interruption = AgentSessionObservationMapper.Current(session.Status);
        var usage = AgentSessionJsonHelper.Usage(session);

        return new UnifiedSessionSummaryDto(
            Id: session.Id,
            Source: sourceKind!,
            RuntimeSessionId: session.Status.AgentRuntimeSessionId,
            Runtime: session.Runtime.Runtime,
            Activity: activity,
            CreatedAt: session.Status.CreatedAt.ToString("o"),
            LastActivityAt: AgentSessionJsonHelper.LastActivityAt(session).ToString("o"),
            Model: session.Settings.Model,
            ResolvedModel: transcriptSummary.ResolvedModel,
            FailureCategory: interruption is null ? transcriptSummary.FailureCategory : null,
            FailureReason: interruption is null ? transcriptSummary.FailureReason : null,
            ToolCallCount: transcriptSummary.ToolCallCount,
            ToolErrorCount: transcriptSummary.ToolErrorCount,
            AgentId: isAgentSession ? record.Label(GenericAgentSessionMetadata.AgentId) : null,
            AgentName: isAgentSession ? record.Label(GenericAgentSessionMetadata.AgentName) : null,
            WorkflowRunId: isWorkflow ? record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId) : null,
            SessionName: isWorkflow ? record.Label(AgentSessionQueryMetadataKeys.SessionName) : null,
            ContextRefs: BuildUnifiedContextRefs(record),
            Usage: AgentSessionDtoMapper.ToUsageDto(usage),
            RecoveryAvailable: IsRecoveryAvailable(session),
            CurrentTurnId: CurrentTurnId(session),
            Inputs: AgentSessionObservationMapper.Inputs(session.Status),
            Turns: AgentSessionObservationMapper.Turns(session.Status),
            RecoveryHistory: transcriptSummary.RecoveryHistory,
            Interruption: interruption,
            InterruptionHistory: AgentSessionObservationMapper.History(session.Status),
            Origin: record.Label(GenericAgentSessionMetadata.Origin),
            TargetId: record.Label(GenericAgentSessionMetadata.TargetId),
            AppliedReasoningEffort: transcriptSummary.AppliedReasoningEffort);
    }
}
