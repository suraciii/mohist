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
    private readonly TimeProvider _timeProvider;

    public AgentSessionQuerier(IDbContextFactory<MohistDbContext> dbFactory, AgentSessionQuery sessionQuery, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _sessionQuery = sessionQuery;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByWorkflowAsync(string workflowRunId, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels((AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)),
            ct: ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var terminalFacts = await LoadTerminalFactsAsync(db, sessions.Select(r => r.Session.Id), ct);
        return sessions.Select(record => ToWorkflowDto(record, terminalFacts.GetValueOrDefault(record.Session.Id))).ToList();
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
        var transcript = await LoadTranscriptAsync(db, session.Session.Id, ct);
        return new WorkflowSessionDetailDto(ToWorkflowDto(session, TerminalFact.FromTranscript(transcript)), SessionTranscriptBuilder.Build(transcript));
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())),
            ct: ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var terminalFacts = await LoadTerminalFactsAsync(db, sessions.Select(r => r.Session.Id), ct);
        return sessions.Select(record => ToWorkflowDto(record, terminalFacts.GetValueOrDefault(record.Session.Id))).ToList();
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
    /// requested limit (issued-130 T-002 / design D2). Composes the three
    /// indexed labels (<c>project-id</c>, <c>agent-id</c>,
    /// <c>source-kind = agent-launch</c>) so the DB query cannot leak
    /// workflow sessions or another agent's sessions. Terminal status
    /// (<c>completed</c> / <c>failed</c> / <c>stopped</c>) is resolved per
    /// session by reusing <see cref="LoadTerminalFactsAsync"/>;
    /// <c>running</c> maps to "opened + no terminal fact + AgentSessionId
    /// present". The optional <paramref name="statusSet"/> is applied as
    /// an in-memory filter over the indexed result set so it composes
    /// with <paramref name="additionalContextLabels"/> (DB-level filters
    /// resolved against the T-001 indexed columns). Each returned item
    /// carries enough information for the workbench to derive the four
    /// primary state groupings (recent / running / failed / ended)
    /// directly from the response.
    /// </summary>
    /// <remarks>
    /// The cap is clamped to <c>[1, 200]</c>; the default <c>50</c>
    /// matches the established project-wide list
    /// (<see cref="ListCurrentAsync"/>) and the active-agents readout
    /// (<see cref="AgentActivityFeedAssembler.GetActivityAsync"/>, which
    /// absorbed the activity-feed projection in issue-327 T-003). Status
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
        var terminalFacts = await LoadTerminalFactsAsync(db, sessionIds, ct);
        var eventSummaries = await TranscriptReductions.LoadEventSummariesAsync(db, sessionIds, ct);

        var items = records.Select(record =>
        {
            var s = record.Session;
            var fact = terminalFacts.GetValueOrDefault(s.Id);
            var summary = eventSummaries.GetValueOrDefault(s.Id);
            var status = ResolveAgentSessionListStatus(record, fact);
            return new AgentSessionListItemDto(
                s.Id,
                record.Label(GenericAgentSessionMetadata.AgentId) ?? string.Empty,
                record.Label(GenericAgentSessionMetadata.AgentName) ?? string.Empty,
                status,
                s.Status.CreatedAt.ToString("o"),
                AgentSessionJsonHelper.LastActivityAt(s).ToString("o"),
                summary?.ResolvedModel,
                BuildAgentSessionListContextRefs(record));
        }).ToList();

        if (statusSet is { Count: > 0 })
        {
            var set = new HashSet<string>(statusSet, StringComparer.OrdinalIgnoreCase);
            items = items.Where(i => set.Contains(i.Status)).ToList();
        }

        return items;
    }

    /// <summary>
    /// Lists generic <c>agent-launch</c> sessions that carry a specific
    /// context-reference label (issue-number or epic-number), returning a
    /// lightweight association list for the issue/epic association endpoints
    /// (issue-130 T-006). Filters by <c>(project-id, source-kind=agent-launch,
    /// {labelKey}={labelValue})</c> using the T-001 indexed columns. Session
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

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessionIds = records.Select(r => r.Session.Id).ToArray();
        var terminalFacts = await LoadTerminalFactsAsync(db, sessionIds, ct);

        return records.Select(record =>
        {
            var s = record.Session;
            var fact = terminalFacts.GetValueOrDefault(s.Id);
            var status = ResolveAgentSessionListStatus(record, fact);
            return new AgentSessionContextAssociationDto(
                s.Id,
                record.Label(GenericAgentSessionMetadata.AgentId) ?? string.Empty,
                record.Label(GenericAgentSessionMetadata.AgentName) ?? string.Empty,
                status,
                s.Status.CreatedAt.ToString("o"),
                $"/api/projects/{projectRef}/agent-sessions/{s.Id}");
        }).ToList();
    }

    /// <summary>
    /// Resolves the workbench-vocabulary status for an agent-scoped list
    /// entry (issued-130 T-002 / design D2). Terminal facts take
    /// precedence; <c>running</c> requires the runner to have bound
    /// <see cref="AgentSessionRow.AgentSessionId"/> and no terminal fact
    /// to be present; anything else is <c>pending</c>. The runner
    /// protocol's legacy <c>cancelled</c> alias is normalised to the
    /// spec vocabulary's <c>stopped</c>.
    /// </summary>
    private static string ResolveAgentSessionListStatus(AgentSessionRecord record, TerminalFact? fact)
    {
        if (fact is not null)
        {
            return string.Equals(fact.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                ? "stopped"
                : fact.Status;
        }
        return record.Row.AgentSessionId is not null ? "running" : "pending";
    }

    /// <summary>
    /// Builds the optional <see cref="AgentSessionListContextRefsDto"/>
    /// envelope from the labels stamped at launch. Returns <c>null</c>
    /// when the session carried no context references so the wire
    /// response omits the field instead of fabricating an empty object
    /// (issued-130 T-002: "absent rather than null", per design D4).
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

        var terminalState = await ReadTerminalStateAsync(db, session.Id, ct);

        return new FollowupTarget(
            runnerId,
            workflowRunId,
            record.Label(AgentSessionQueryMetadataKeys.SessionName) ?? sessionName,
            terminalState,
            AgentSessionJsonHelper.StatusName(session, Now()) == "active");
    }

    /// <summary>
    /// Resolves the runner + activity state of a generic (non-workflow)
    /// <see cref="AgentSession"/> for followup delivery (issue-129 T-004).
    /// Distinct from <see cref="ResolveFollowupTargetAsync"/> which is
    /// issue-anchored and returns null when the workflow-run label is
    /// blank: generic sessions are addressed by their minted sessionId
    /// alone, and the launch endpoint stamps
    /// <c>source-kind = agent-launch</c> labels (no workflow-run lookup
    /// key). The runner id is sourced from the grain's runtime (the
    /// runner's <c>open</c> call is what stamps it onto the session after
    /// the launch mints it with an empty RunnerId, per T-003). Active
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
        var terminalState = await ReadTerminalStateAsync(db, sessionId, ct);

        return new GenericFollowupTarget(
            runnerId ?? string.Empty,
            sessionId,
            terminalState,
            AgentSessionJsonHelper.StatusName(record.Session, Now()) == "active");
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
            record.Row.RunnerId ?? string.Empty,
            record.Session.Id,
            sourceKind!,
            workflowRunId,
            sessionName,
            await ReadTerminalStateAsync(db, sessionId, ct));
    }

    /// <summary>
    /// Resolves the cancel target for a generic (non-workflow)
    /// <see cref="AgentSession"/> (issue-129 T-005). Distinct from
    /// <see cref="ResolveGenericFollowupTargetAsync"/>: cancel is
    /// best-effort over ACP, so the endpoint needs the runner id AND the
    /// terminal-state verdict up front — if the session is already terminal
    /// the server short-circuits without calling the runner at all. The
    /// returned <see cref="GenericCancelTarget.TerminalState"/> is the
    /// verbatim <c>status</c> field of the most recent
    /// <c>session.closed</c> transcript event
    /// (<c>completed</c> / <c>failed</c> / <c>stopped</c>), so the HTTP
    /// response can mirror the runner's reported state without inventing a
    /// value.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when the session is unknown OR belongs to a
    /// different project (cross-project leakage guard), matching the
    /// null-return contract <see cref="ResolveGenericFollowupTargetAsync"/>
    /// uses for the same cases.
    /// </remarks>
    public async Task<GenericCancelTarget?> ResolveGenericCancelTargetAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var records = await _sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null) return null;

        var sessionProjectId = record.Label(AgentSessionQueryMetadataKeys.ProjectId);
        if (!string.Equals(sessionProjectId, projectId, StringComparison.Ordinal))
            return null;

        var runnerId = record.Row.RunnerId;
        var terminalState = await ReadTerminalStateAsync(db, sessionId, ct);

        return new GenericCancelTarget(
            runnerId ?? string.Empty,
            sessionId,
            terminalState);
    }

    /// <summary>
    /// Reads the most recent <c>session.closed</c> transcript event and
    /// returns its <c>status</c> field
    /// (<c>completed</c> / <c>failed</c> / <c>stopped</c>), or <c>null</c>
    /// when no terminal event has been recorded yet. Used by
    /// <see cref="ResolveGenericCancelTargetAsync"/> to short-circuit the
    /// cancel endpoint on already-terminal sessions (issue-129 T-005).
    /// </summary>
    private static async Task<string?> ReadTerminalStateAsync(MohistDbContext db, string sessionId, CancellationToken ct)
    {
        var turnIds = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (turnIds.Count == 0) return null;

        var closed = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => turnIds.Contains(p.TurnId)
                && p.Type == TranscriptPartTypes.SessionClosed)
            .OrderByDescending(p => p.Sequence)
            .ThenByDescending(p => p.Id)
            .Select(p => p.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(closed)) return null;

        JsonElement payload;
        try { payload = JSON.DeserializeElement(closed); }
        catch { return null; }
        if (payload.ValueKind != JsonValueKind.Object) return null;

        var status = AgentSessionJsonHelper.GetStringProp(payload, "status");
        if (string.IsNullOrWhiteSpace(status)) return null;
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return status.ToLowerInvariant();
        }
        return null;
    }

    public async Task<AgentSessionMetadataDto?> GetSessionMetadataAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        return await BuildSessionMetadataDtoAsync(db, session, sessionName, ct);
    }

    public async Task<AgentSessionTranscriptResponse?> GetSessionTranscriptAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var transcript = await LoadTranscriptAsync(db, session.Session.Id, ct);
        return SessionTranscriptBuilder.Build(transcript);
    }

    /// <summary>
    /// Builds the generic-session summary surfaced by
    /// <c>GET /api/projects/{projectRef}/agent-sessions/{sessionId}</c>
    /// (issue-130 T-003 / design D4). Returns <c>null</c> when the
    /// session id does not resolve to a generic <c>agent-launch</c>
    /// session in the requested project — the cross-project guard
    /// matches <see cref="ResolveGenericFollowupTargetAsync"/> and
    /// <see cref="ResolveGenericCancelTargetAsync"/> so the caller never
    /// observes a session from a different project.
    /// </summary>
    /// <remarks>
    /// The DTO omits workflow-only fields (workflowRunId, sessionName,
    /// workId, workType, stage) by construction — the record does not
    /// declare them. Status uses the spec vocabulary (<c>running</c> /
    /// <c>completed</c> / <c>failed</c> / <c>stopped</c>) resolved by
    /// the same terminal-fact + bound state logic that powers
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

        var summary = TranscriptEventSummaryProjector.Summarize(
            transcriptEvents.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson)));

        var terminalFacts = await LoadTerminalFactsAsync(db, new[] { session.Id }, ct);
        var status = ResolveAgentSessionListStatus(record, terminalFacts.GetValueOrDefault(session.Id));

        var usage = AgentSessionJsonHelper.Usage(session);

        return new GenericAgentSessionSummaryDto(
            session.Id,
            record.Label(GenericAgentSessionMetadata.AgentId) ?? string.Empty,
            record.Label(GenericAgentSessionMetadata.AgentName) ?? string.Empty,
            session.Status.AgentRuntimeSessionId,
            session.Runtime.Runtime,
            status,
            session.Status.CreatedAt.ToString("o"),
            AgentSessionJsonHelper.LastActivityAt(session).ToString("o"),
            summary.ResolvedModel,
            summary.FailureCategory,
            summary.ToolCallCount,
            summary.ToolErrorCount,
            BuildGenericSessionSummaryContextRefs(record),
            AgentSessionDtoMapper.ToUsageDto(usage),
            AgentSessionDtoMapper.BuildLineageDto(session),
            AgentSessionJsonHelper.StatusName(session, Now()) != "active");
    }

    public async Task<AgentSessionTranscriptResponse?> GetGenericSessionTranscriptAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindGenericSessionAsync(projectId, sessionId, ct);
        if (session is null) return null;

        var transcript = await LoadTranscriptAsync(db, session.Session.Id, ct);
        return SessionTranscriptBuilder.Build(transcript);
    }

    /// <summary>
    /// Builds the optional <see cref="GenericAgentSessionSummaryContextRefsDto"/>
    /// envelope from the labels stamped at launch (issue-130 T-003).
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
        var eventSummary = TranscriptEventSummaryProjector.Summarize(
            transcriptEvents.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson)));
        var toolCount = eventSummary.ToolCallCount ?? 0;
        var usage = AgentSessionJsonHelper.Usage(domainSession);
        var lineage = AgentSessionDtoMapper.BuildLineageDto(domainSession);

        return new AgentSessionMetadataDto(
            domainSession.Id,
            session.Label(AgentSessionQueryMetadataKeys.SessionName) ?? fallbackSessionName,
            domainSession.Status.AgentRuntimeSessionId,
            domainSession.Runtime.Runtime,
            AgentSessionJsonHelper.StatusName(domainSession, Now()),
            domainSession.Settings.Model,
            session.Label(AgentSessionQueryMetadataKeys.Stage),
            domainSession.Metadata.Annotation(AgentSessionQueryMetadataKeys.Title),
            domainSession.Status.CreatedAt.ToString("o"),
            null,
            AgentSessionDtoMapper.ToEventSummaryDto(eventSummary),
            AgentSessionDtoMapper.ToUsageDto(usage),
            new AgentSessionMetadataCounts(partCount, toolCount),
            lineage);
    }

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

    private WorkflowSessionDto ToWorkflowDto(AgentSessionRecord record, TerminalFact? terminalFact = null)
    {
        var s = record.Session;
        var issueNumber = record.IssueNumber();
        var status = terminalFact?.Status ?? (AgentSessionJsonHelper.StatusName(s, Now()) == "active" ? "running" : "inactive");
        return new(
        s.Id,
        record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId) ?? string.Empty,
        record.Label(AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
        s.Status.AgentRuntimeSessionId,
        s.Runtime.Runtime,
        record.Label(AgentSessionQueryMetadataKeys.ProjectId),
        issueNumber == 0 ? null : issueNumber,
        s.Runtime.RunnerId,
        status, record.Label(AgentSessionQueryMetadataKeys.Stage), s.Settings.Model, s.Runtime.WorkDir, null,
        s.Status.CreatedAt.ToString("o"), s.Status.BoundAt?.ToString("o"), s.Status.LastDataAt?.ToString("o"),
        terminalFact?.CompletedAt.ToString("o"), terminalFact?.FailureReason, terminalFact?.ExitCode,
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
            AgentSessionJsonHelper.StatusName(s, Now()), s.Status.CreatedAt.ToString("o"), null,
            s.Settings.Model, s.Runtime.Runtime, record.Label(AgentSessionQueryMetadataKeys.Stage), s.Metadata.Annotation(AgentSessionQueryMetadataKeys.Title),
            s.Status.LastDataAt?.ToString("o"), null, null, null,
            new AgentEventSummaryDto(null, null, null, null, null, null),
            AgentSessionDtoMapper.ToUsageDto(s));
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private static async Task<AgentSessionTranscriptData> LoadTranscriptAsync(MohistDbContext db, string sessionId, CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, new[] { sessionId }, ct: ct);
        var turns = loaded.Turns
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToList();
        var parts = loaded.Parts
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToList();
        return new AgentSessionTranscriptData(turns, parts);
    }

    private static async Task<Dictionary<string, TerminalFact>> LoadTerminalFactsAsync(
        MohistDbContext db,
        IEnumerable<string> sessionIds,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, sessionIds, ct, partType: TranscriptPartTypes.SessionClosed);
        if (loaded.Parts.Count == 0) return [];

        var result = new Dictionary<string, TerminalFact>(StringComparer.Ordinal);
        foreach (var part in loaded.Parts.OrderBy(part => part.Sequence).ThenBy(part => part.Id))
        {
            if (!loaded.SessionByTurnId.TryGetValue(part.TurnId, out var sessionId)) continue;
            var fact = TerminalFact.FromPart(part);
            if (fact is not null) result[sessionId] = fact;
        }
        return result;
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
    public string SessionId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }
}

internal sealed record AgentSessionTranscriptData(
    IReadOnlyList<AgentSessionTranscriptTurnRow> Turns,
    IReadOnlyList<AgentSessionTranscriptPartRow> Parts);

internal sealed record TerminalFact(
    string Status,
    DateTime CompletedAt,
    string? FailureReason,
    int? ExitCode)
{
    public static TerminalFact? FromTranscript(AgentSessionTranscriptData transcript) => transcript.Parts
        .Where(part => part.Type == TranscriptPartTypes.SessionClosed)
        .OrderBy(part => part.Sequence)
        .ThenBy(part => part.Id)
        .Select(FromPart)
        .LastOrDefault(fact => fact is not null);

    public static TerminalFact? FromPart(AgentSessionTranscriptPartRow part)
    {
        var payload = AgentSessionJsonHelper.ParsePayload(part.PayloadJson);
        var status = AgentSessionJsonHelper.GetStringProp(payload, "status") ?? "completed";
        // issued-130 T-002: accept "stopped" alongside the legacy
        // "cancelled" alias. The runner protocol uses "cancelled" today;
        // "stopped" is the spec vocabulary used by the agent workbench
        // groupings (design D2). Loading both lets the in-memory status
        // filter surface either name without changing the write path.
        if (status is not ("completed" or "failed" or "cancelled" or "stopped")) return null;
        return new TerminalFact(
            status,
            part.LastSeenAt,
            AgentSessionJsonHelper.GetStringProp(payload, "failureReason"),
            AgentSessionJsonHelper.GetIntProp(payload, "exitCode"));
    }
}

public sealed record FollowupTarget(
    string RunnerId,
    string WorkflowRunId,
    string SessionName,
    string? TerminalState,
    bool IsActive);

/// <summary>
/// Followup target for a generic (non-workflow) <see cref="AgentSession"/>
/// (issue-129 T-004). Identifies a session by its minted
/// <see cref="SessionId"/> alone — there is no <c>workflowRunId</c> /
/// <c>sessionName</c> pair to carry. The runner uses
/// <see cref="SessionId"/> to look up the active ACP session entry under
/// the <c>generic:</c> prefix in <c>AcpSessionManager</c>.
/// </summary>
public sealed record GenericFollowupTarget(
    string RunnerId,
    string SessionId,
    string? TerminalState,
    bool IsActive);

public sealed record SessionCancelTarget(
    string RunnerId,
    string SessionId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? TerminalState);

/// <summary>
/// Cancel target for a generic (non-workflow) <see cref="AgentSession"/>
/// (issue-129 T-005). Carries the runner id (so the server can resolve
/// the runner's SignalR connection) and the most-recent terminal state
/// observed in the session's transcript (so the endpoint can short-circuit
/// without ever invoking the runner when the session is already
/// <c>completed</c> / <c>failed</c> / <c>stopped</c>). <see cref="TerminalState"/>
/// is <c>null</c> when the session is not yet terminal, in which case the
/// endpoint must call the runner and let it report the cancellation
/// outcome (design D6).
/// </summary>
public sealed record GenericCancelTarget(
    string RunnerId,
    string SessionId,
    string? TerminalState);
