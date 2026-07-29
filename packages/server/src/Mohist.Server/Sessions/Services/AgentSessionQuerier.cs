using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Queries <see cref="AgentSession"/> records in the context of workflow runs.
/// </summary>
/// <remarks>
/// <see cref="AgentSession"/> is a peer-level aggregate root, NOT a child of <see cref="Mohist.Server.Workflow.Domain.Run.WorkflowRun"/>.
/// The association between a session and a workflow run is by reference only — a <see cref="Mohist.Server.Workflow.Domain.Run.TaskRun"/>
/// refers to a session and the run is the aggregate root that contains that task.
/// No ownership relationship exists.
/// </remarks>
public class AgentSessionQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentSessionQuery _sessionQuery;

    public AgentSessionQuerier(IDbContextFactory<MohistDbContext> dbFactory, AgentSessionQuery sessionQuery)
    {
        _dbFactory = dbFactory;
        _sessionQuery = sessionQuery;
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByWorkflowAsync(string workflowRunId, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels((AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)),
            ct: ct);
        return sessions.Select(ToWorkflowDto).ToList();
    }

    public async Task<WorkflowSessionDetailDto?> GetByWorkflowAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        var session = await _sessionQuery.FirstByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId),
                (AgentSessionQueryMetadataKeys.SessionName, sessionName)),
            ct: ct);
        if (session is null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var transcript = await LoadTranscriptAsync(db, session.Session.Id, null, ct);
        return new WorkflowSessionDetailDto(ToWorkflowDto(session), SessionTranscriptBuilder.Build(transcript));
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())),
            ct: ct);
        return sessions.Select(ToWorkflowDto).ToList();
    }

    public async Task<IReadOnlyList<AgentSessionSummaryDto>> ListSummariesByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())),
            ct: ct);
        return sessions.Select(ToSummaryDto).ToList();
    }

    /// <summary>
    /// Lists generic (non-workflow) <see cref="AgentSession"/>s for an Agent
    /// profile within a project, recency-ordered and capped at the
    /// requested limit. Composes the three
    /// indexed labels (<c>project-id</c>, <c>agent-id</c>,
    /// <c>source-kind = agent-launch</c>) so the DB query cannot leak
    /// workflow sessions or another agent's sessions. Terminal status
    /// (<c>completed</c> / <c>failed</c> / <c>stopped</c>) is resolved per
    /// session by reusing <see cref="LoadTerminalFactsAsync"/>;
    /// <c>running</c> maps to "opened + no terminal fact + AgentSessionId
    /// present". The optional <paramref name="statusSet"/> is applied as
    /// an in-memory filter over the indexed result set so it composes
    /// with <paramref name="additionalContextLabels"/> (DB-level filters
    /// resolved against the indexed columns). Each returned item
    /// carries enough information for the workbench to derive the four
    /// primary state groupings (recent / running / failed / ended)
    /// directly from the response.
    /// </summary>
    /// <remarks>
    /// The cap is clamped to <c>[1, 200]</c>; the default <c>50</c>
    /// matches the established project-wide list
    /// (<see cref="ListCurrentAsync"/>) and the active-agents readout
    /// (<see cref="AgentActivityFeedAssembler.GetActivityAsync"/>, which
    /// absorbed the activity-feed projection). Status
    /// vocabulary: <c>running</c> / <c>completed</c> / <c>failed</c> /
    /// <c>stopped</c>. The legacy runner protocol's <c>cancelled</c>
    /// alias is normalised to <c>stopped</c> at this read boundary.
    /// Sessions whose runner has not yet bound <c>AgentSessionId</c> and
    /// which have no terminal fact appear as <c>pending</c> and surface
    /// only in the <c>recent</c> grouping.
    /// </remarks>
    public async Task<IReadOnlyList<AgentSessionListItemDto>> ListAgentSessionsAsync(
        string projectId,
        string agentId,
        IReadOnlyCollection<string>? statusSet = null,
        int limit = 50,
        IReadOnlyDictionary<string, string>? additionalContextLabels = null,
        CancellationToken ct = default)
    {
        var clampedLimit = Math.Clamp(limit, 1, 200);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
        };
        if (additionalContextLabels is not null)
        {
            foreach (var (key, value) in additionalContextLabels)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    labels[key] = value;
            }
        }

        var records = await _sessionQuery.ListByLabelsAsync(
            labels,
            AgentSessionQueryOrder.CreatedDescending,
            clampedLimit,
            ct: ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessionIds = records.Select(r => r.Session.Id).ToArray();
        var eventSummaries = await TranscriptReductions.LoadEventSummariesAsync(db, sessionIds, ct);

        var items = records.Select(record =>
        {
            var s = record.Session;
            var summary = eventSummaries.GetValueOrDefault(s.Id);
            var activity = ResolveAgentSessionActivity(record);
            return new AgentSessionListItemDto(
                s.Id,
                record.Label(GenericAgentSessionMetadata.AgentId) ?? string.Empty,
                record.Label(GenericAgentSessionMetadata.AgentName) ?? string.Empty,
                activity,
                s.Status.CreatedAt.ToString("o"),
                AgentSessionJsonHelper.LastActivityAt(s).ToString("o"),
                summary?.ResolvedModel,
                BuildAgentSessionListContextRefs(record));
        }).ToList();

        if (statusSet is { Count: > 0 })
        {
            var set = new HashSet<string>(statusSet, StringComparer.OrdinalIgnoreCase);
            items = items.Where(i => set.Contains(i.Activity)).ToList();
        }

        return items;
    }

    /// <summary>
    /// Lists generic <c>agent-launch</c> sessions that carry a specific
    /// context-reference label (issue-number or epic-number), returning a
    /// lightweight association list for the issue/epic association endpoints
    ///. Filters by <c>(project-id, source-kind=agent-launch,
    /// {labelKey}={labelValue})</c> using the indexed columns. Session
    /// status is resolved via the same terminal-fact logic as
    /// <see cref="ListAgentSessionsAsync"/>. Each returned entry includes a
    /// relative URL link back to the session summary route
    /// (<c>/api/projects/{projectRef}/agent-sessions/{sessionId}</c>).
    /// Empty result (no matching sessions) returns <c>[]</c>.
    /// </summary>
    /// <remarks>
    /// The method performs no writes and creates no scope/mount/supervisor/
    /// ownership lifecycle — the association is a pure read derived from
    /// labels the launcher already stamps. The <paramref name="projectRef"/>
    /// parameter is the raw route <c>{projectRef}</c> value used to build
    /// the <see cref="AgentSessionContextAssociationDto.SessionLink"/>;
    /// it may differ from the internal <paramref name="projectId"/>.
    /// </remarks>
    public async Task<IReadOnlyList<AgentSessionContextAssociationDto>> ListSessionsByContextRefAsync(
        string projectId,
        string projectRef,
        string labelKey,
        string labelValue,
        CancellationToken ct = default)
    {
        var records = await _sessionQuery.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [labelKey] = labelValue,
            },
            AgentSessionQueryOrder.CreatedDescending,
            ct: ct);

        return records.Select(record =>
        {
            var s = record.Session;
            var activity = ResolveAgentSessionActivity(record);
            return new AgentSessionContextAssociationDto(
                s.Id,
                record.Label(GenericAgentSessionMetadata.AgentId) ?? string.Empty,
                record.Label(GenericAgentSessionMetadata.AgentName) ?? string.Empty,
                activity,
                s.Status.CreatedAt.ToString("o"),
                $"/api/projects/{projectRef}/agent-sessions/{s.Id}");
        }).ToList();
    }

    private static string ResolveAgentSessionActivity(AgentSessionRecord record) =>
        AgentSessionJsonHelper.ActivityName(record.Session);

    /// <summary>
    /// Builds the optional <see cref="AgentSessionListContextRefsDto"/>
    /// envelope from the labels stamped at launch. Returns <c>null</c>
    /// when the session carried no context references so the wire
    /// response omits the field instead of fabricating an empty object
    /// ("absent rather than null").
    /// </summary>
    private static AgentSessionListContextRefsDto? BuildAgentSessionListContextRefs(AgentSessionRecord record)
    {
        var refs = AgentSessionContextRefs.TryBuild(record);
        return refs is null
            ? null
            : new AgentSessionListContextRefsDto(refs.Value.IssueNumber, refs.Value.EpicNumber, refs.Value.Repository, refs.Value.WorkspacePath);
    }

    public async Task<string?> ResolveIssueSessionIdAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        return record?.Session.Id;
    }

    public async Task<FollowupTarget?> ResolveFollowupTargetAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (record is null) return null;

        var session = record.Session;
        var runnerId = record.Row.RunnerId;
        var workflowRunId = record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
        if (string.IsNullOrWhiteSpace(runnerId) || string.IsNullOrWhiteSpace(workflowRunId))
            return null;

        return new FollowupTarget(
            runnerId,
            workflowRunId,
            record.Label(AgentSessionQueryMetadataKeys.SessionName) ?? sessionName,
            AgentSessionJsonHelper.ActivityName(session) == "active");
    }

    /// <summary>
    /// Resolves the runner + activity state of a generic (non-workflow)
    /// <see cref="AgentSession"/> for followup delivery.
    /// Distinct from <see cref="ResolveFollowupTargetAsync"/> which is
    /// issue-anchored and returns null when the workflow-run label is
    /// blank: generic sessions are addressed by their minted sessionId
    /// alone, and the launch endpoint stamps
    /// <c>source-kind = agent-launch</c> labels (no workflow-run lookup
    /// key). The runner id is sourced from the grain's runtime (the
    /// runner's <c>open</c> call is what stamps it onto the session after
    /// the launch mints it with an empty RunnerId, ). Active
    /// state mirrors <see cref="AgentSessionJsonHelper.StatusName"/>.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when the session has not yet been opened by a
    /// runner (no RunnerId bound) — the same null result the issue-scoped
    /// resolver returns for sessions without a workflowRunId, so the
    /// endpoint maps null to 404 and the caller does not need to know
    /// which lookup axis was missing.
    /// </remarks>
    public async Task<GenericFollowupTarget?> ResolveGenericFollowupTargetAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var records = await _sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null) return null;

        var sessionProjectId = record.Label(AgentSessionQueryMetadataKeys.ProjectId);
        if (!string.Equals(sessionProjectId, projectId, StringComparison.Ordinal))
            return null;

        // The session exists and belongs to the requested project. The
        // RunnerId may be empty if the launch minted the session but the
        // runner never opened it — that state is "inactive" from the
        // followup perspective, not "not found". Surface IsActive=false
        // and let the endpoint return 409 (matching the issue-scoped
        // followup behaviour for inactive sessions).
        var runnerId = record.Row.RunnerId;
        return new GenericFollowupTarget(
            runnerId ?? string.Empty,
            sessionId,
            AgentSessionJsonHelper.ActivityName(record.Session) == "active");
    }

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
        else if (!string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal))
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
                : null);
    }

    public async Task<SessionCancelTarget?> ResolveCancelTargetAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var records = await _sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null || !string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal))
            return null;

        var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind);
        var workflowRunId = record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
        var sessionName = record.Label(AgentSessionQueryMetadataKeys.SessionName);
        if (string.Equals(sourceKind, "workflow", StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(sessionName)))
            return null;
        if (!string.Equals(sourceKind, "workflow", StringComparison.Ordinal)
            && !string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal))
            return null;

        return new SessionCancelTarget(
            record.Session.Runtime.RunnerId,
            record.Session.Id,
            sourceKind!,
            workflowRunId,
            sessionName,
            record.Session.Runtime.Runtime,
            record.Session.Status.AgentRuntimeSessionId,
            record.Session.Runtime.WorkDir);
    }

    public async Task<AgentSessionMetadataDto?> GetSessionMetadataAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindReadableSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        return await BuildSessionMetadataDtoAsync(db, session, sessionName, ct);
    }

    public async Task<AgentSessionTranscriptResponse?> GetSessionTranscriptAsync(
        string projectId,
        int issueNumber,
        string sessionName,
        string? runtimeSessionId = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindReadableSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var transcript = await LoadTranscriptAsync(db, session.Session.Id, runtimeSessionId, ct);
        return SessionTranscriptBuilder.Build(transcript);
    }

    /// <summary>
    /// Builds the generic-session summary surfaced by
    /// <c>GET /api/projects/{projectRef}/agent-sessions/{sessionId}</c>
    ///. Returns <c>null</c> when the
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
            summary.FailureCategory,
            summary.FailureReason,
            summary.ToolCallCount,
            summary.ToolErrorCount,
            BuildGenericSessionSummaryContextRefs(record),
            AgentSessionDtoMapper.ToUsageDto(usage),
            session.Status.Activity == AgentSessionActivity.Idle,
            CurrentTurnId(session));
    }

    private static bool IsApplicableToCurrentRuntime(
        TranscriptEventProjection projection,
        IReadOnlyDictionary<long, long> turnSequenceByTurnId,
        string? currentRuntimeSessionId,
        TranscriptPartLoaderResult loaded)
    {
        _ = turnSequenceByTurnId;
        if (string.IsNullOrWhiteSpace(currentRuntimeSessionId)) return true;
        var turn = loaded.Turns.FirstOrDefault(t => t.Id == projection.TurnId);
        return turn is not null
            && string.Equals(turn.RuntimeSessionId, currentRuntimeSessionId, StringComparison.Ordinal);
    }

    public async Task<AgentSessionTranscriptResponse?> GetGenericSessionTranscriptAsync(
        string projectId,
        string sessionId,
        string? runtimeSessionId = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindGenericSessionAsync(projectId, sessionId, ct);
        if (session is null) return null;

        var transcript = await LoadTranscriptAsync(db, session.Session.Id, runtimeSessionId, ct);
        return SessionTranscriptBuilder.Build(transcript);
    }

    /// <summary>
    /// Builds the unified source-agnostic session summary surfaced by
    /// <c>GET /api/projects/{projectRef}/sessions/{sessionId}</c>.
    /// Resolves the row by id WITHOUT the
    /// <c>source-kind == agent-launch</c> gate applied by
    /// <see cref="FindGenericSessionAsync"/> — a workflow-originated session
    /// resolves here by the same stable id as an agent-launch session. The
    /// cross-project guard matches <see cref="ResolveCanonicalFollowupTargetAsync"/>
    /// so the caller never observes a session from a different project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The summary branches internally on the resolved
    /// <see cref="AgentSessionQueryMetadataKeys.SourceKind"/> to populate
    /// source-specific identity only for its source: agent-launch sessions
    /// carry <c>agentId</c> / <c>agentName</c>; workflow sessions carry
    /// <c>workflowRunId</c> / <c>sessionName</c>. The absent-when-empty idiom
    /// (<see cref="Infrastructure.JSON.Options"/>) omits the unused branch's
    /// fields from the wire rather than nulling them.
    /// </para>
    /// <para>
    /// Returns <c>null</c> when the session id does not resolve, when the
    /// session belongs to a different project, or when the row carries an
    /// unknown source kind — the caller maps null to 404.
    /// </para>
    /// </remarks>
    public async Task<UnifiedSessionSummaryDto?> GetUnifiedSessionSummaryAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        var record = await FindUnifiedSessionAsync(projectId, sessionId, ct);
        if (record is null) return null;

        var session = record.Session;
        var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind);
        var isWorkflow = string.Equals(sourceKind, "workflow", StringComparison.Ordinal);
        var isAgentLaunch = string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal);
        if (!isWorkflow && !isAgentLaunch) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var resolvedModel = await ResolveModelAsync(db, session.Id, session, ct);

        var activity = ResolveAgentSessionActivity(record);
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
            ResolvedModel: resolvedModel,
            AgentId: isAgentLaunch ? record.Label(GenericAgentSessionMetadata.AgentId) : null,
            AgentName: isAgentLaunch ? record.Label(GenericAgentSessionMetadata.AgentName) : null,
            WorkflowRunId: isWorkflow ? record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId) : null,
            SessionName: isWorkflow ? record.Label(AgentSessionQueryMetadataKeys.SessionName) : null,
            ContextRefs: BuildUnifiedContextRefs(record),
            Usage: AgentSessionDtoMapper.ToUsageDto(usage));
    }

    /// <summary>
    /// Builds the unified source-agnostic transcript surfaced by
    /// <c>GET /api/projects/{projectRef}/sessions/{sessionId}/transcript</c>.
    /// Resolves the row by id WITHOUT the
    /// <c>source-kind == agent-launch</c> gate, so a workflow-originated
    /// session's transcript resolves here by the same stable id as an
    /// agent-launch session's. Returns <c>null</c> for an unknown id, a
    /// cross-project session, or an unknown source kind.
    /// </summary>
    public async Task<AgentSessionTranscriptResponse?> GetUnifiedSessionTranscriptAsync(
        string projectId,
        string sessionId,
        string? runtimeSessionId = null,
        CancellationToken ct = default)
    {
        var record = await FindUnifiedSessionAsync(projectId, sessionId, ct);
        if (record is null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var transcript = await LoadTranscriptAsync(db, record.Session.Id, runtimeSessionId, ct);
        return SessionTranscriptBuilder.Build(transcript);
    }

    /// <summary>
    /// Resolves a session row by id without the <c>source-kind</c> gate and
    /// enforces project isolation. Returns <c>null</c> when the id does not
    /// resolve, the session belongs to a different project, or the source
    /// kind is neither <c>agent-launch</c> nor <c>workflow</c>.
    /// </summary>
    private async Task<AgentSessionRecord?> FindUnifiedSessionAsync(string projectId, string sessionId, CancellationToken ct)
    {
        var records = await _sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null) return null;

        if (!string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal))
            return null;

        var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind);
        if (!string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal)
            && !string.Equals(sourceKind, "workflow", StringComparison.Ordinal))
            return null;

        return record;
    }

    /// <summary>
    /// Resolves the model name for a unified summary. For sessions with a
    /// transcript-persisted resolved model, prefers the latest transcript
    /// model event; otherwise falls back to the session's declared
    /// <see cref="AgentSessionSettings.Model"/>.
    /// </summary>
    private async Task<string?> ResolveModelAsync(
        MohistDbContext db,
        string sessionId,
        AgentSession session,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, new[] { sessionId }, ct: ct);
        var projections = ToTranscriptProjectionsInSequenceOrder(loaded);
        var summary = TranscriptEventSummaryProjector.Summarize(
            projections.Select(e => new TranscriptSummaryEvent(
                TurnSequence: 0,
                Sequence: e.Sequence,
                PartId: e.Id.ToString(),
                Type: e.Type,
                PayloadJson: e.PayloadJson)));
        return summary.ResolvedModel ?? session.Settings.Model;
    }

    /// <summary>
    /// Builds the optional <see cref="UnifiedSessionContextRefsDto"/>
    /// envelope from the labels recorded on the session. Reads both the
    /// agent-launch context labels and the workflow issue-number label so
    /// the unified read surfaces context consistently across sources.
    /// Returns <c>null</c> when the session carried no context reference.
    /// </summary>
    private static UnifiedSessionContextRefsDto? BuildUnifiedContextRefs(AgentSessionRecord record)
    {
        var agentRefs = AgentSessionContextRefs.TryBuild(record);
        var workflowIssue = record.IssueNumber();

        var issueNumber = agentRefs?.IssueNumber ?? (workflowIssue > 0 ? workflowIssue : null);
        var epicNumber = agentRefs?.EpicNumber;
        var repository = agentRefs?.Repository;
        var workspacePath = agentRefs?.WorkspacePath;

        if (issueNumber is null && epicNumber is null
            && string.IsNullOrWhiteSpace(repository) && string.IsNullOrWhiteSpace(workspacePath))
            return null;

        return new UnifiedSessionContextRefsDto(issueNumber, epicNumber, repository, workspacePath);
    }

    /// <summary>
    /// Builds the optional <see cref="GenericAgentSessionSummaryContextRefsDto"/>
    /// envelope from the labels stamped at launch.
    /// Returns <c>null</c> when the session carried no context references
    /// so the wire response omits the field instead of fabricating an
    /// empty object — mirroring the agent-scoped list's
    /// <see cref="BuildAgentSessionListContextRefs"/> invariant.
    /// </summary>
    private static GenericAgentSessionSummaryContextRefsDto? BuildGenericSessionSummaryContextRefs(AgentSessionRecord record)
    {
        var refs = AgentSessionContextRefs.TryBuild(record);
        return refs is null
            ? null
            : new GenericAgentSessionSummaryContextRefsDto(refs.Value.IssueNumber, refs.Value.EpicNumber, refs.Value.Repository, refs.Value.WorkspacePath);
    }

    private async Task<AgentSessionMetadataDto> BuildSessionMetadataDtoAsync(
        MohistDbContext db,
        AgentSessionRecord session,
        string fallbackSessionName,
        CancellationToken ct)
    {
        var domainSession = session.Session;
        var loaded = await TranscriptPartLoader.LoadAsync(db, new[] { domainSession.Id }, ct: ct);
        var transcriptEvents = ToTranscriptProjectionsInSequenceOrder(loaded);
        var partCount = transcriptEvents.Count;
        var turnSequenceByTurnId = loaded.Turns.ToDictionary(t => t.Id, t => t.Sequence);
        var eventSummary = TranscriptEventSummaryProjector.Summarize(
            transcriptEvents.Select(e => new TranscriptSummaryEvent(
                TurnSequence: turnSequenceByTurnId.GetValueOrDefault(e.TurnId, 0),
                Sequence: e.Sequence,
                PartId: e.Id.ToString(),
                Type: e.Type,
                PayloadJson: e.PayloadJson)));
        var toolCount = eventSummary.ToolCallCount ?? 0;
        var usage = AgentSessionJsonHelper.Usage(domainSession);

        return new AgentSessionMetadataDto(
            domainSession.Id,
            session.Label(AgentSessionQueryMetadataKeys.SessionName) ?? fallbackSessionName,
            domainSession.Status.AgentRuntimeSessionId,
            domainSession.Runtime.Runtime,
            AgentSessionJsonHelper.ActivityName(domainSession),
            domainSession.Settings.Model,
            session.Label(AgentSessionQueryMetadataKeys.Stage),
            domainSession.Metadata.Annotation(AgentSessionQueryMetadataKeys.Title),
            domainSession.Status.CreatedAt.ToString("o"),
            null,
            AgentSessionDtoMapper.ToEventSummaryDto(eventSummary),
            AgentSessionDtoMapper.ToUsageDto(usage),
            new AgentSessionMetadataCounts(partCount, toolCount),
            CurrentTurnId(domainSession));
    }

    private static string? CurrentTurnId(AgentSession session) =>
        session.Status.Turns?
            .LastOrDefault(turn => turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing)
            ?.Id;

    private async Task<AgentSessionRecord?> FindCurrentSessionAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        string sessionName,
        CancellationToken ct)
    {
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number == issueNumber)
            .ToListAsync(ct);
        var workflowRunId = rows
            .Select(row => ReadWorkflowRunId(row.State))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (workflowRunId is null) return null;

        return await _sessionQuery.FirstByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString()),
                (AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId),
                (AgentSessionQueryMetadataKeys.SessionName, sessionName)),
            ct: ct);
    }

    private async Task<AgentSessionRecord?> FindReadableSessionAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        string sessionName,
        CancellationToken ct)
    {
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number == issueNumber)
            .ToListAsync(ct);
        var workflowRunId = rows
            .Select(row => ReadWorkflowRunId(row.State))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        if (!string.IsNullOrWhiteSpace(workflowRunId))
        {
            return await _sessionQuery.FirstByLabelsAsync(
                AgentSessionDtoMapper.Labels(
                    (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                    (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString()),
                    (AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId),
                    (AgentSessionQueryMetadataKeys.SessionName, sessionName)),
                ct: ct);
        }

        return await _sessionQuery.FirstByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString()),
                (AgentSessionQueryMetadataKeys.SourceKind, "workflow"),
                (AgentSessionQueryMetadataKeys.SessionName, sessionName)),
            AgentSessionQueryOrder.CreatedDescending,
            ct: ct);
    }

    private async Task<AgentSessionRecord?> FindGenericSessionAsync(string projectId, string sessionId, CancellationToken ct)
    {
        var records = await _sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null) return null;

        return string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal)
            && string.Equals(record.Label(AgentSessionQueryMetadataKeys.SourceKind), "agent-launch", StringComparison.Ordinal)
            ? record
            : null;
    }

    private static string? ReadWorkflowRunId(string stateJson)
    {
        try
        {
            using var document = JsonDocument.Parse(stateJson);
            return document.RootElement.TryGetProperty("workflowRunId", out var workflowRunId)
                && workflowRunId.ValueKind == JsonValueKind.String
                ? workflowRunId.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private WorkflowSessionDto ToWorkflowDto(AgentSessionRecord record)
    {
        var s = record.Session;
        var issueNumber = record.IssueNumber();
        var activity = ResolveAgentSessionActivity(record);
        return new(
        s.Id,
        record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId) ?? string.Empty,
        record.Label(AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
        s.Status.AgentRuntimeSessionId,
        s.Runtime.Runtime,
        record.Label(AgentSessionQueryMetadataKeys.ProjectId),
        issueNumber == 0 ? null : issueNumber,
        s.Runtime.RunnerId,
        activity, record.Label(AgentSessionQueryMetadataKeys.Stage), s.Settings.Model, s.Runtime.WorkDir, null,
        s.Status.CreatedAt.ToString("o"), s.Status.BoundAt?.ToString("o"), s.Status.LastDataAt?.ToString("o"),
        null, null, null,
        new AgentEventSummaryDto(null, null, null, null, null, null),
        AgentSessionDtoMapper.ToUsageDto(s));
    }

    private AgentSessionSummaryDto ToSummaryDto(AgentSessionRecord record)
    {
        var s = record.Session;
        return new AgentSessionSummaryDto(
            s.Id,
            record.Label(AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
            s.Status.AgentRuntimeSessionId,
            record.Label(AgentSessionQueryMetadataKeys.WorkId),
            s.Metadata.Annotation(AgentSessionQueryMetadataKeys.Title),
            AgentSessionJsonHelper.ActivityName(s), s.Status.CreatedAt.ToString("o"), null,
            s.Settings.Model, s.Runtime.Runtime, record.Label(AgentSessionQueryMetadataKeys.Stage), s.Metadata.Annotation(AgentSessionQueryMetadataKeys.Title),
            s.Status.LastDataAt?.ToString("o"), null, null, null,
            new AgentEventSummaryDto(null, null, null, null, null, null),
            AgentSessionDtoMapper.ToUsageDto(s),
            record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
    }

    private static async Task<AgentSessionTranscriptData> LoadTranscriptAsync(
        MohistDbContext db,
        string sessionId,
        string? runtimeSessionId,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, new[] { sessionId }, ct: ct);
        var turns = loaded.Turns
            .Where(turn => string.IsNullOrWhiteSpace(runtimeSessionId)
                || string.Equals(turn.RuntimeSessionId, runtimeSessionId, StringComparison.Ordinal))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToList();
        var turnIds = turns.Select(turn => turn.Id).ToHashSet();
        var parts = loaded.Parts
            .Where(part => turnIds.Contains(part.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToList();
        return new AgentSessionTranscriptData(turns, parts);
    }

    private static IReadOnlyList<TranscriptEventProjection> ToTranscriptProjectionsInSequenceOrder(TranscriptPartLoaderResult loaded) =>
        loaded.Parts
            .Where(part => loaded.SessionByTurnId.ContainsKey(part.TurnId))
            .OrderBy(part => part.Sequence)
            .ThenBy(part => part.Id)
            .Select(part => AgentSessionDtoMapper.ToProjection(loaded.SessionByTurnId[part.TurnId], part))
            .ToList();
}

internal sealed record TranscriptEventProjection
{
    public long Id { get; init; }
    public long TurnId { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }
}

internal sealed record AgentSessionTranscriptData(
    IReadOnlyList<AgentSessionTranscriptTurnRow> Turns,
    IReadOnlyList<AgentSessionTranscriptPartRow> Parts);

public sealed record FollowupTarget(
    string RunnerId,
    string WorkflowRunId,
    string SessionName,
    bool IsActive);

/// <summary>
/// Followup target for a generic (non-workflow) <see cref="AgentSession"/>
///. Identifies a session by its minted
/// <see cref="SessionId"/> alone — there is no <c>workflowRunId</c> /
/// <c>sessionName</c> pair to carry. The runner resolves the session
/// through the OpenCode runtime's <c>generic:</c>-prefixed binding
/// lookup at Follow-up / Cancel dispatch time.
/// </summary>
public sealed record GenericFollowupTarget(
    string RunnerId,
    string SessionId,
    bool IsActive);

public sealed record CanonicalFollowupTarget(
    string RunnerId,
    string SessionId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir,
    AgentExecutionDefinition? Definition = null);

public sealed record SessionCancelTarget(
    string RunnerId,
    string SessionId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir);
