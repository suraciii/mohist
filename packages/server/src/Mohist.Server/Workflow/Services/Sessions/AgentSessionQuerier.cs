using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services.Sessions;

/// <summary>
/// Queries <see cref="AgentSession"/> records in the context of workflow runs.
/// </summary>
/// <remarks>
/// <see cref="AgentSession"/> is a peer-level aggregate root, NOT a child of <see cref="WorkflowRun"/>.
/// The association between a session and a workflow run is by reference only — a <see cref="TaskRun"/>
/// refers to a session and the run is the aggregate root that contains that task.
/// No ownership relationship exists.
/// </remarks>
public class AgentSessionQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowQuerier _workflowQuerier;
    private readonly AgentSessionQuery _sessionQuery;
    private readonly TimeProvider _timeProvider;

    public AgentSessionQuerier(IDbContextFactory<MohistDbContext> dbFactory, WorkflowQuerier workflowQuerier, AgentSessionQuery sessionQuery, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _workflowQuerier = workflowQuerier;
        _sessionQuery = sessionQuery;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByWorkflowAsync(string workflowRunId, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            Labels((AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)),
            ct: ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var terminalFacts = await LoadTerminalFactsAsync(db, sessions.Select(r => r.Session.Id), ct);
        return sessions.Select(record => ToWorkflowDto(record, terminalFacts.GetValueOrDefault(record.Session.Id))).ToList();
    }

    public async Task<WorkflowSessionDetailDto?> GetByWorkflowAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        var session = await _sessionQuery.FirstByLabelsAsync(
            Labels(
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
            Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())),
            ct: ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var terminalFacts = await LoadTerminalFactsAsync(db, sessions.Select(r => r.Session.Id), ct);
        return sessions.Select(record => ToWorkflowDto(record, terminalFacts.GetValueOrDefault(record.Session.Id))).ToList();
    }

    public async Task<IReadOnlyList<AgentSessionInfoDto>> ListCurrentAsync(string projectId, string? status = null, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = await _sessionQuery.ListByLabelsAsync(
            Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedDescending,
            limit,
            status: status,
            ct: ct);
        sessions = await ReconcileActiveSessionsAsync(db, sessions, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, sessions.Select(IssueNumber), ct);
        var eventSummaries = await LoadEventSummariesAsync(db, sessions.Select(r => r.Session.Id), ct);
        return sessions.Select(record =>
        {
            var s = record.Session;
            var events = eventSummaries.GetValueOrDefault(s.Id);
            var usage = AgentSessionJsonHelper.Usage(s);
            var issueNumber = IssueNumber(record);
            return new AgentSessionInfoDto(
            issueNumber,
            IssueTitle(issueTitles, issueNumber),
            Label(record, AgentSessionQueryMetadataKeys.Stage) ?? string.Empty,
            s.Id,
            AgentSessionJsonHelper.StatusName(s, Now()),
            s.Settings.Model,
            null,
            s.Status.CreatedAt.ToString("o"),
            null,
            AgentSessionJsonHelper.LastActivityAt(s).ToString("o"),
            ToEventSummaryDto(events),
            ToUsageDto(usage));
        }).ToList();
    }

    public async Task<IReadOnlyList<AgentSessionSummaryDto>> ListSummariesByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            Labels(
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
    /// (<see cref="GetActivityAsync"/>). Status vocabulary: <c>running</c>
    /// / <c>completed</c> / <c>failed</c> / <c>stopped</c>. The legacy
    /// runner protocol's <c>cancelled</c> alias is normalised to
    /// <c>stopped</c> at this read boundary. Sessions whose runner has
    /// not yet bound <c>AgentSessionId</c> and which have no terminal
    /// fact appear as <c>pending</c> and surface only in the
    /// <c>recent</c> grouping.
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
        var eventSummaries = await LoadEventSummariesAsync(db, sessionIds, ct);

        var items = records.Select(record =>
        {
            var s = record.Session;
            var fact = terminalFacts.GetValueOrDefault(s.Id);
            var summary = eventSummaries.GetValueOrDefault(s.Id);
            var status = ResolveAgentSessionListStatus(record, fact);
            return new AgentSessionListItemDto(
                s.Id,
                Label(record, GenericAgentSessionMetadata.AgentId) ?? string.Empty,
                Label(record, GenericAgentSessionMetadata.AgentName) ?? string.Empty,
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
                Label(record, GenericAgentSessionMetadata.AgentId) ?? string.Empty,
                Label(record, GenericAgentSessionMetadata.AgentName) ?? string.Empty,
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
        var issueNumberText = Label(record, GenericAgentSessionMetadata.IssueNumber);
        var issueNumber = int.TryParse(issueNumberText, out var parsed) ? parsed : (int?)null;
        var epicNumber = Label(record, GenericAgentSessionMetadata.EpicNumber);
        var repository = Label(record, GenericAgentSessionMetadata.Repository);
        var workspacePath = Label(record, GenericAgentSessionMetadata.WorkspacePath);

        if (issueNumber is null && string.IsNullOrWhiteSpace(epicNumber)
            && string.IsNullOrWhiteSpace(repository) && string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        return new AgentSessionListContextRefsDto(issueNumber, epicNumber, repository, workspacePath);
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
        var workflowRunId = Label(record, AgentSessionQueryMetadataKeys.WorkflowRunId);
        if (string.IsNullOrWhiteSpace(runnerId) || string.IsNullOrWhiteSpace(workflowRunId))
            return null;

        return new FollowupTarget(
            runnerId,
            workflowRunId,
            Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? sessionName,
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

        var sessionProjectId = Label(record, AgentSessionQueryMetadataKeys.ProjectId);
        if (!string.Equals(sessionProjectId, projectId, StringComparison.Ordinal))
            return null;

        // The session exists and belongs to the requested project. The
        // RunnerId may be empty if the launch minted the session but the
        // runner never opened it — that state is "inactive" from the
        // followup perspective, not "not found". Surface IsActive=false
        // and let the endpoint return 409 (matching the issue-scoped
        // followup behaviour for inactive sessions).
        var runnerId = record.Row.RunnerId;
        var statusActive = !string.IsNullOrWhiteSpace(runnerId)
            && await ReadTerminalStateAsync(db, sessionId, ct) is null
            && AgentSessionJsonHelper.StatusName(record.Session, Now()) == "active";

        return new GenericFollowupTarget(
            runnerId ?? string.Empty,
            sessionId,
            statusActive);
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

        var sessionProjectId = Label(record, AgentSessionQueryMetadataKeys.ProjectId);
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
    /// Reads the most recent <c>session.closed</c> / <c>session_closed</c>
    /// transcript event and returns its <c>status</c> field
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
                && (p.Type == "session_closed" || p.Type == "session.closed"))
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
            || string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
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
        var transcript = await LoadTranscriptAsync(db, session.Id, ct);
        var sessionByTurnId = transcript.Turns.ToDictionary(t => t.Id, t => t.SessionId);
        var transcriptEvents = transcript.Parts
            .Where(part => sessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => ToProjection(sessionByTurnId[part.TurnId], part))
            .ToList();

        var summary = TranscriptEventSummaryProjector.Summarize(
            transcriptEvents.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson)));

        var terminalFacts = await LoadTerminalFactsAsync(db, new[] { session.Id }, ct);
        var status = ResolveAgentSessionListStatus(record, terminalFacts.GetValueOrDefault(session.Id));

        var usage = AgentSessionJsonHelper.Usage(session);

        return new GenericAgentSessionSummaryDto(
            session.Id,
            Label(record, GenericAgentSessionMetadata.AgentId) ?? string.Empty,
            Label(record, GenericAgentSessionMetadata.AgentName) ?? string.Empty,
            status,
            session.Status.CreatedAt.ToString("o"),
            AgentSessionJsonHelper.LastActivityAt(session).ToString("o"),
            summary.ResolvedModel,
            summary.FailureCategory,
            summary.ToolCallCount,
            summary.ToolErrorCount,
            BuildGenericSessionSummaryContextRefs(record),
            ToUsageDto(usage));
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
        var issueNumberText = Label(record, GenericAgentSessionMetadata.IssueNumber);
        var issueNumber = int.TryParse(issueNumberText, out var parsed) ? parsed : (int?)null;
        var epicNumber = Label(record, GenericAgentSessionMetadata.EpicNumber);
        var repository = Label(record, GenericAgentSessionMetadata.Repository);
        var workspacePath = Label(record, GenericAgentSessionMetadata.WorkspacePath);

        if (issueNumber is null && string.IsNullOrWhiteSpace(epicNumber)
            && string.IsNullOrWhiteSpace(repository) && string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        return new GenericAgentSessionSummaryContextRefsDto(issueNumber, epicNumber, repository, workspacePath);
    }

    private async Task<AgentSessionMetadataDto> BuildSessionMetadataDtoAsync(
        MohistDbContext db,
        AgentSessionRecord session,
        string fallbackSessionName,
        CancellationToken ct)
    {
        var domainSession = session.Session;
        var transcript = await LoadTranscriptAsync(db, domainSession.Id, ct);
        var sessionByTurnId = transcript.Turns.ToDictionary(t => t.Id, t => t.SessionId);
        var transcriptEvents = transcript.Parts
            .Where(part => sessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => ToProjection(sessionByTurnId[part.TurnId], part))
            .ToList();
        var partCount = transcriptEvents.Count;
        var eventSummary = TranscriptEventSummaryProjector.Summarize(
            transcriptEvents.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson)));
        var toolCount = eventSummary.ToolCallCount ?? 0;
        var usage = AgentSessionJsonHelper.Usage(domainSession);
        var lineage = BuildLineageDto(domainSession);

        return new AgentSessionMetadataDto(
            domainSession.Id,
            Label(session, AgentSessionQueryMetadataKeys.SessionName) ?? fallbackSessionName,
            domainSession.Status.AgentRuntimeSessionId ?? domainSession.Id,
            AgentSessionJsonHelper.StatusName(domainSession, Now()),
            domainSession.Settings.Model,
            Label(session, AgentSessionQueryMetadataKeys.Stage),
            Annotation(domainSession, AgentSessionQueryMetadataKeys.Title),
            domainSession.Status.CreatedAt.ToString("o"),
            null,
            ToEventSummaryDto(eventSummary),
            ToUsageDto(usage),
            new AgentSessionMetadataCounts(partCount, toolCount),
            lineage);
    }

    /// <summary>
    /// Builds the <see cref="RuntimeSessionLineageEntryDto"/> projection
    /// from <see cref="AgentSession.Status.RuntimeSessionLineage"/>. When
    /// the grain hasn't yet recorded an explicit lineage (legacy
    /// rehydration) but the session is currently bound, a single entry is
    /// synthesized so the UI can still distinguish "no chain at all"
    /// (historical single binding) from "real chain" (>=2 entries).
    /// Returns <c>null</c> only when there is truly nothing to surface.
    /// </summary>
    internal static IReadOnlyList<RuntimeSessionLineageEntryDto>? BuildLineageDto(AgentSession domainSession)
    {
        var lineage = domainSession.Status.RuntimeSessionLineage;
        if (lineage is not null && lineage.Count > 0)
        {
            return lineage
                .Select(e => new RuntimeSessionLineageEntryDto(
                    e.AgentRuntimeSessionId,
                    e.BoundAt.ToString("o")))
                .ToList();
        }

        if (!string.IsNullOrEmpty(domainSession.Status.AgentRuntimeSessionId))
        {
            var boundAt = domainSession.Status.BoundAt ?? domainSession.Status.CreatedAt;
            return
            [
                new RuntimeSessionLineageEntryDto(
                    domainSession.Status.AgentRuntimeSessionId,
                    boundAt.ToString("o"))
            ];
        }

        return null;
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
        var issue = IssueRowMapper.Deserialize(rows)
            .FirstOrDefault(issue => IssueRowMapper.IsIssue(issue, projectId, issueNumber));
        var workflowRunId = issue?.WorkflowRunId;
        if (workflowRunId is null) return null;

        return await _sessionQuery.FirstByLabelsAsync(
            Labels(
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

        return string.Equals(Label(record, AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal)
            && string.Equals(Label(record, AgentSessionQueryMetadataKeys.SourceKind), "agent-launch", StringComparison.Ordinal)
            ? record
            : null;
    }

    public async Task<ActivityDto> GetActivityAsync(string projectId, int? limit = null, IReadOnlyList<ActivityWaitingCardDto>? waiting = null, RunnerCapacityView? capacity = null, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = await _sessionQuery.ListByLabelsAsync(
                Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
                AgentSessionQueryOrder.CreatedDescending,
                take,
                ct: ct);
        sessions = await ReconcileActiveSessionsAsync(db, sessions, ct);

        var sessionIds = sessions.Select(s => s.Session.Id).ToArray();
        var latestEvents = await LoadLatestEventsAsync(db, sessionIds, ct);
        var eventSummaries = await LoadEventSummariesAsync(db, sessionIds, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, sessions.Select(IssueNumber), ct);
        var taskProgressMap = await BuildTaskProgressMapAsync(sessions, ct);

        var cards = sessions
            .Select(record => ToActivityCard(record, latestEvents.GetValueOrDefault(record.Session.Id), eventSummaries.GetValueOrDefault(record.Session.Id), IssueTitle(issueTitles, IssueNumber(record)), taskProgressMap.GetValueOrDefault(record.Session.Id)))
            .ToList();

        waiting ??= [];
        var slots = new ActivitySlotUsageDto(capacity?.UsedSlots ?? 0, capacity?.TotalSlots ?? 0);
        var summary = new ActivitySummaryDto(
            cards.Count(c => c.Status == "active"),
            waiting.Count,
            0,
            0,
            slots);

        return new ActivityDto(summary, cards, waiting.ToList());
    }

    private async Task<Dictionary<string, ActivityTaskProgressDto>> BuildTaskProgressMapAsync(IReadOnlyList<AgentSessionRecord> sessions, CancellationToken ct)
    {
        var result = new Dictionary<string, ActivityTaskProgressDto>(StringComparer.Ordinal);
        var workflowRunIds = sessions
            .Select(s => Label(s, AgentSessionQueryMetadataKeys.WorkflowRunId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (workflowRunIds.Length == 0) return result;

        var statusTasks = workflowRunIds.Select(async wrid =>
        {
            var status = await _workflowQuerier.GetStatusAsync(wrid);
            return (WorkflowRunId: wrid, Status: status);
        });
        var statuses = await Task.WhenAll(statusTasks);
        var statusByWorkflow = statuses.ToDictionary(x => x.WorkflowRunId, x => x.Status, StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            var workflowRunId = Label(session, AgentSessionQueryMetadataKeys.WorkflowRunId);
            if (workflowRunId is null || !statusByWorkflow.TryGetValue(workflowRunId, out var status) || status is null)
                continue;

            var currentStageId = status.CurrentStage;
            if (string.IsNullOrWhiteSpace(currentStageId))
                continue;

            var stage = status.Stages.FirstOrDefault(s => string.Equals(s.Stage, currentStageId, StringComparison.OrdinalIgnoreCase));
            if (stage is null)
                continue;

            var completed = stage.Tasks.Count(t => string.Equals(t.Status, "completed", StringComparison.OrdinalIgnoreCase));
            var total = stage.Tasks.Count;
            if (total > 0)
                result[session.Session.Id] = new ActivityTaskProgressDto(completed, total);
        }

        return result;
    }

    public async Task<AgentUsageTimeseriesDto> GetUsageTimeseriesAsync(string projectId, CancellationToken ct = default)
    {
        var rangeTo = Now().Date.AddDays(1);
        var rangeFrom = rangeTo.AddDays(-7);

        var windowSessions = await _sessionQuery.ListByLabelsAsync(
            Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedAscending,
            from: rangeFrom,
            to: rangeTo,
            ct: ct);

        var buckets = new UsageBucketData[7];
        for (var i = 0; i < 7; i++)
            buckets[i] = new UsageBucketData(rangeFrom.AddDays(i), rangeFrom.AddDays(i + 1));

        foreach (var record in windowSessions)
        {
            var usage = AgentSessionJsonHelper.Usage(record.Session);
            if (!HasUsage(usage)) continue;

            var createdAt = record.Session.Status.CreatedAt;
            var bucketIndex = (int)(createdAt.Date - rangeFrom.Date).Days;
            if (bucketIndex < 0 || bucketIndex >= 7) continue;

            var bucket = buckets[bucketIndex];
            var costAmount = usage.CostAmount ?? 0d;
            bucket.InputTokens += usage.InputTokens ?? 0;
            bucket.OutputTokens += usage.OutputTokens ?? 0;
            bucket.TotalTokens += usage.TotalTokens ?? 0;
            bucket.CostAmount += costAmount;
            bucket.CostCurrency ??= usage.CostCurrency;
            bucket.SampleCount++;
        }

        var preWindow = await ComputePreWindowSpendAsync(projectId, rangeFrom, ct);

        var cumulative = await ComputeCumulativeCostPerShipAsync(
            projectId, rangeFrom, preWindow.Spend, preWindow.Samples, preWindow.Currency, buckets, ct);

        return new AgentUsageTimeseriesDto(
            rangeFrom,
            rangeTo,
            "day",
            buckets.Select(b => b.ToDto()).ToList(),
            cumulative);
    }

    private sealed record PreWindowSpendResult(double Spend, int Samples, string? Currency);

    private async Task<PreWindowSpendResult> ComputePreWindowSpendAsync(string projectId, DateTime rangeFrom, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.LabelProjectId == projectId && s.CreatedAt < rangeFrom)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        double spend = 0;
        int samples = 0;
        string? currency = null;

        foreach (var row in rows)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is null) continue;

            var usage = AgentSessionJsonHelper.Usage(session);
            if (!HasUsage(usage)) continue;

            spend += usage.CostAmount ?? 0d;
            samples++;
            currency ??= usage.CostCurrency;
        }

        return new PreWindowSpendResult(spend, samples, currency);
    }

    private async Task<IReadOnlyList<CumulativeCostPerShipPointDto>> ComputeCumulativeCostPerShipAsync(
        string projectId,
        DateTime rangeFrom,
        double preWindowSpend,
        int preWindowSamples,
        string? currency,
        UsageBucketData[] buckets,
        CancellationToken ct)
    {
        List<DateTime> shippedDates;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.Issues.AsNoTracking()
                .Where(row => row.ProjectId == projectId)
                .ToListAsync(ct);

            shippedDates = IssueRowMapper.Deserialize(rows)
                .Where(issue => issue.Status == IssueStatus.Done && issue.CompletedAt.HasValue)
                .Select(issue => issue.CompletedAt!.Value)
                .ToList();
        }

        var preWindowShipped = shippedDates.Count(d => d < rangeFrom);
        var result = new List<CumulativeCostPerShipPointDto>(7);
        double cumulativeCost = preWindowSpend;
        int cumulativeSamples = preWindowSamples;
        var cumulativeShipped = preWindowShipped;
        string? resolvedCurrency = currency;

        for (var i = 0; i < 7; i++)
        {
            var dayStart = rangeFrom.AddDays(i);
            var dayEnd = rangeFrom.AddDays(i + 1);

            cumulativeCost += buckets[i].CostAmount;
            cumulativeSamples += buckets[i].SampleCount;
            resolvedCurrency ??= buckets[i].CostCurrency;

            var dayShipped = shippedDates.Count(d => d >= dayStart && d < dayEnd);
            cumulativeShipped += dayShipped;

            double? costForDay = cumulativeSamples > 0 || cumulativeShipped > 0 ? cumulativeCost : null;
            double? costPerShip = cumulativeShipped > 0
                ? cumulativeCost / cumulativeShipped
                : null;

            result.Add(new CumulativeCostPerShipPointDto(
                dayEnd,
                costForDay,
                cumulativeSamples > 0 ? resolvedCurrency : null,
                cumulativeShipped,
                costPerShip));
        }

        return result;
    }

    public async Task<AgentCostRollupRawData> GetCostRollupAsync(string projectId, CancellationToken ct = default)
    {
        var allSessions = await _sessionQuery.ListByLabelsAsync(
            Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedAscending,
            ct: ct);

        var todayStart = Now().Date;
        var todayEnd = todayStart.AddDays(1);

        double totalCost = 0d;
        int totalSamples = 0;
        string? totalCurrency = null;

        double todayCost = 0d;
        int todaySamples = 0;
        string? todayCurrency = null;

        foreach (var record in allSessions)
        {
            var usage = AgentSessionJsonHelper.Usage(record.Session);
            if (!HasUsage(usage)) continue;

            var costAmount = usage.CostAmount ?? 0d;

            totalCost += costAmount;
            totalSamples++;
            totalCurrency ??= usage.CostCurrency;

            var createdAt = record.Session.Status.CreatedAt;
            if (createdAt >= todayStart && createdAt < todayEnd)
            {
                todayCost += costAmount;
                todaySamples++;
                todayCurrency ??= usage.CostCurrency;
            }
        }

        return new AgentCostRollupRawData(
            new AgentCostMetricDto(totalSamples > 0 ? totalCost : null, totalCurrency, totalSamples),
            new AgentCostMetricDto(todaySamples > 0 ? todayCost : null, todayCurrency, todaySamples));
    }

    private static bool HasUsage(AgentUsageSummary usage)
    {
        return usage.InputTokens.HasValue
            || usage.OutputTokens.HasValue
            || usage.TotalTokens.HasValue
            || usage.CostAmount.HasValue;
    }

    private sealed class UsageBucketData
    {
        private readonly DateTime _bucketStart;
        private readonly DateTime _bucketEnd;

        public long InputTokens;
        public long OutputTokens;
        public long TotalTokens;
        public double CostAmount;
        public string? CostCurrency;
        public int SampleCount;

        public UsageBucketData(DateTime bucketStart, DateTime bucketEnd)
        {
            _bucketStart = bucketStart;
            _bucketEnd = bucketEnd;
        }

        public UsageBucketDto ToDto() => new(
            _bucketStart,
            _bucketEnd,
            InputTokens,
            OutputTokens,
            TotalTokens,
            CostAmount,
            CostCurrency);
    }

    private static async Task<Dictionary<string, TranscriptEventProjection>> LoadLatestEventsAsync(
        MohistDbContext db, string[] sessionIds, CancellationToken ct)
    {
        if (sessionIds.Length == 0) return [];

        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => sessionIds.Contains(t.SessionId))
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        if (turnIds.Length == 0) return [];

        var sessionByTurnId = turns.ToDictionary(t => t.Id, t => t.SessionId);
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.LastSeenAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var result = new Dictionary<string, TranscriptEventProjection>(StringComparer.Ordinal);
        foreach (var part in parts)
            if (sessionByTurnId.TryGetValue(part.TurnId, out var sessionId))
                result[sessionId] = ToProjection(sessionId, part);

        return result;
    }

    private static async Task<Dictionary<string, AgentSessionTranscriptSummary>> LoadEventSummariesAsync(
        MohistDbContext db, IEnumerable<string> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];

        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => ids.Contains(t.SessionId))
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        if (turnIds.Length == 0) return [];

        var sessionByTurnId = turns.ToDictionary(t => t.Id, t => t.SessionId);
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return parts
            .Where(part => sessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => ToProjection(sessionByTurnId[part.TurnId], part))
            .OrderBy(e => e.Sequence)
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => TranscriptEventSummaryProjector.Summarize(
                g.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson))), StringComparer.Ordinal);
    }

    private ActivityCardDto ToActivityCard(AgentSessionRecord record, TranscriptEventProjection? latestEvent, AgentSessionTranscriptSummary? eventSummary, string issueTitle, ActivityTaskProgressDto? taskProgress)
    {
        var s = record.Session;
        var lastActivityAt = AgentSessionJsonHelper.LastActivityAt(s).ToString("o");
        var issueNumber = IssueNumber(record);
        var projectId = Label(record, AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty;
        var sessionName = Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? s.Id;
        var stage = Label(record, AgentSessionQueryMetadataKeys.Stage);
        var workId = Label(record, AgentSessionQueryMetadataKeys.WorkId);
        var workType = Label(record, AgentSessionQueryMetadataKeys.WorkType);
        var sourceKind = Label(record, AgentSessionQueryMetadataKeys.SourceKind);

        if (string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal))
        {
            var agentId = Label(record, GenericAgentSessionMetadata.AgentId) ?? string.Empty;
            var agentName = Label(record, GenericAgentSessionMetadata.AgentName) ?? string.Empty;
            return new ActivityCardDto(
                $"agent_{agentId}",
                issueNumber,
                issueTitle,
                stage ?? string.Empty,
                null,
                s.Id,
                AgentSessionJsonHelper.StatusName(s, Now()),
                s.Settings.Model,
                null,
                s.Status.CreatedAt.ToString("o"),
                null,
                lastActivityAt,
                new ActivityWorkItemDto(workType ?? "task", workId ?? sessionName, workId ?? sessionName, stage, null),
                taskProgress,
                latestEvent is null ? null : ToPreview(latestEvent),
                null,
                agentId,
                agentName,
                ToEventSummaryDto(eventSummary),
                ToUsageDto(s));
        }

        return new ActivityCardDto(
            $"issue_{projectId}_{issueNumber}",
            issueNumber,
            issueTitle,
            stage ?? string.Empty,
            null,
            s.Id,
            AgentSessionJsonHelper.StatusName(s, Now()),
            s.Settings.Model,
            null,
            s.Status.CreatedAt.ToString("o"),
            null,
            lastActivityAt,
            new ActivityWorkItemDto(workType ?? "task", workId ?? sessionName, workId ?? sessionName, stage, null),
            taskProgress,
            latestEvent is null ? null : ToPreview(latestEvent),
            null,
            null,
            null,
            ToEventSummaryDto(eventSummary),
            ToUsageDto(s));
    }

    private static async Task<Dictionary<int, string>> LoadIssueTitlesAsync(
        MohistDbContext db,
        string projectId,
        IEnumerable<int> issueNumbers,
        CancellationToken ct)
    {
        var numbers = issueNumbers.Distinct().ToArray();
        if (numbers.Length == 0) return [];

        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number != null && numbers.Contains(row.Number.Value))
            .ToListAsync(ct);

        return IssueRowMapper.ByNumber(rows, projectId, numbers)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Title);
    }

    private static string IssueTitle(IReadOnlyDictionary<int, string> titles, int issueNumber) =>
        titles.TryGetValue(issueNumber, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : $"Issue #{issueNumber}";

    private static ActivityPreviewDto ToPreview(TranscriptEventProjection e)
    {
        var text = ExtractPreviewText(e.PayloadJson);
        var kind = e.Type.Contains("tool", StringComparison.OrdinalIgnoreCase) ? "tool" : "text";
        return new ActivityPreviewDto(kind, string.IsNullOrWhiteSpace(text) ? e.Type : Truncate(text, 120), e.CreatedAt.ToString("o"));
    }

    private static string ExtractPreviewText(string json)
    {
        try
        {
            var payload = JSON.DeserializeElement(json);
            if (payload.ValueKind == JsonValueKind.String) return payload.GetString() ?? string.Empty;
            if (payload.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var key in new[] { "title", "toolName", "text", "message", "command" })
            {
                if (payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? string.Empty;
            }
            if (payload.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("text", out var contentText)
                && contentText.ValueKind == JsonValueKind.String)
                return contentText.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "\u2026";

    private AgentSessionDto ToAgentSessionDto(AgentSessionRecord record)
    {
        var s = record.Session;
        var usage = AgentSessionJsonHelper.Usage(s);
        return new AgentSessionDto(
            s.Id,
            Label(record, AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
            IssueNumber(record),
            Label(record, AgentSessionQueryMetadataKeys.WorkflowRunId) ?? string.Empty,
            Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
            Label(record, AgentSessionQueryMetadataKeys.WorkId),
            Label(record, AgentSessionQueryMetadataKeys.WorkType),
            Label(record, AgentSessionQueryMetadataKeys.Stage),
            Annotation(s, AgentSessionQueryMetadataKeys.Title), s.Runtime.RunnerId, s.Status.AgentRuntimeSessionId,
            AgentSessionJsonHelper.StatusName(s, Now()), s.Settings.Model, s.Runtime.WorkDir, null, null,
            s.Status.CreatedAt.ToString("o"), s.Status.BoundAt?.ToString("o"), null,
            s.Status.LastDataAt?.ToString("o"), null, null,
            new AgentEventSummaryDto(null, null, null, null, null, null),
            ToUsageDto(usage));
    }

    private WorkflowSessionDto ToWorkflowDto(AgentSessionRecord record, TerminalFact? terminalFact = null)
    {
        var s = record.Session;
        var issueNumber = IssueNumber(record);
        var status = terminalFact?.Status ?? (AgentSessionJsonHelper.StatusName(s, Now()) == "active" ? "running" : "inactive");
        return new(
        s.Id,
        Label(record, AgentSessionQueryMetadataKeys.WorkflowRunId) ?? string.Empty,
        Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
        s.Status.AgentRuntimeSessionId,
        Label(record, AgentSessionQueryMetadataKeys.ProjectId),
        issueNumber == 0 ? null : issueNumber,
        s.Runtime.RunnerId,
        status, Label(record, AgentSessionQueryMetadataKeys.Stage), s.Settings.Model, s.Runtime.WorkDir, null,
        s.Status.CreatedAt.ToString("o"), s.Status.BoundAt?.ToString("o"), s.Status.LastDataAt?.ToString("o"),
        terminalFact?.CompletedAt.ToString("o"), terminalFact?.FailureReason, terminalFact?.ExitCode,
        new AgentEventSummaryDto(null, null, null, null, null, null),
        ToUsageDto(s));
    }

    private AgentSessionSummaryDto ToSummaryDto(AgentSessionRecord record)
    {
        var s = record.Session;
        return new AgentSessionSummaryDto(
            s.Id,
            Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
            s.Status.AgentRuntimeSessionId ?? s.Id,
            Label(record, AgentSessionQueryMetadataKeys.WorkId),
            Annotation(s, AgentSessionQueryMetadataKeys.Title),
            AgentSessionJsonHelper.StatusName(s, Now()), s.Status.CreatedAt.ToString("o"), null,
            s.Settings.Model, null, Label(record, AgentSessionQueryMetadataKeys.Stage), Annotation(s, AgentSessionQueryMetadataKeys.Title),
            s.Status.LastDataAt?.ToString("o"), null, null, null,
            new AgentEventSummaryDto(null, null, null, null, null, null),
            ToUsageDto(s));
    }

    private static string? Label(AgentSessionRecord record, string key) =>
        record.Label(key) ?? record.Session.Metadata.Label(key);

    private static int IssueNumber(AgentSessionRecord record) =>
        int.TryParse(Label(record, AgentSessionQueryMetadataKeys.IssueNumber), out var issueNumber)
            ? issueNumber
            : 0;

    private static string? Annotation(AgentSession session, string key) => session.Metadata.Annotation(key);

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Same overload as <see cref="ToUsageDto(AgentUsageSummary)"/> but reads
    /// the session's bounded <c>ContextUsageHistory</c> so the activity DTO
    /// can carry the trend data (issue-245 T-002 / design D5). Internal so
    /// the Fake tests can exercise the same projection path that builds
    /// <c>ActivityCardDto.Usage</c> on the wire.
    /// </summary>
    internal static AgentUsageDto ToUsageDto(AgentSession s) =>
        ToUsageDto(AgentSessionJsonHelper.Usage(s), BuildUsageHistoryDto(s));

    private static AgentUsageDto ToUsageDto(AgentUsageSummary u) =>
        new(u.InputTokens, u.OutputTokens, u.TotalTokens, u.CachedReadTokens, u.ThoughtTokens,
            u.CostAmount, u.CostCurrency, u.ContextWindowUsed, u.ContextWindowSize,
            AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize),
            ContextHealthClassifier.Classify(AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize)));

    private static AgentUsageDto ToUsageDto(AgentUsageSummary u, IReadOnlyList<ContextUsageHistoryEntryDto>? history) =>
        new(u.InputTokens, u.OutputTokens, u.TotalTokens, u.CachedReadTokens, u.ThoughtTokens,
            u.CostAmount, u.CostCurrency, u.ContextWindowUsed, u.ContextWindowSize,
            AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize),
            ContextHealthClassifier.Classify(AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize)),
            history);

    /// <summary>
    /// Builds the <see cref="ContextUsageHistoryEntryDto"/> projection from
    /// <see cref="AgentSession.Status.ContextUsageHistory"/>. Returns
    /// <c>null</c> when the session has not yet recorded any usage
    /// (grain never thinned a sample) so the wire stays quiet for
    /// historical/legacy sessions. An empty list is projected as
    /// <c>null</c> for the same reason (issue-245 T-002 / design D5).
    /// </summary>
    internal static IReadOnlyList<ContextUsageHistoryEntryDto>? BuildUsageHistoryDto(AgentSession domainSession)
    {
        var history = domainSession.Status.ContextUsageHistory;
        if (history is null || history.Count == 0) return null;

        return history
            .Select(e => new ContextUsageHistoryEntryDto(e.At.ToString("o"), e.Percent))
            .ToList();
    }

    private static AgentEventSummaryDto ToEventSummaryDto(AgentSessionTranscriptSummary? s) =>
        s is null
            ? new AgentEventSummaryDto(null, null, null, null, null, null)
            : new(
                s.ResolvedModel,
                s.FailureCategory,
                string.Equals(s.FailureCategory, ContextExhaustionClassifier.ContextExhaustionCategory, StringComparison.Ordinal) ? true : null,
                string.Equals(s.FailureCategory, ContextExhaustionClassifier.SuspectedContextExhaustionCategory, StringComparison.Ordinal) ? true : null,
                s.ToolCallCount,
                s.ToolErrorCount);

    private static IReadOnlyDictionary<string, string> Labels(params (string Key, string? Value)[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            result[key] = value;
        }
        return result;
    }

    private static async Task<AgentSessionTranscriptData> LoadTranscriptAsync(MohistDbContext db, string sessionId, CancellationToken ct)
    {
        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);
        return new AgentSessionTranscriptData(turns, parts);
    }

    private static async Task<Dictionary<string, TerminalFact>> LoadTerminalFactsAsync(
        MohistDbContext db,
        IEnumerable<string> sessionIds,
        CancellationToken ct)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];

        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => ids.Contains(t.SessionId))
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        if (turnIds.Length == 0) return [];

        var sessionByTurnId = turns.ToDictionary(t => t.Id, t => t.SessionId);
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(part => turnIds.Contains(part.TurnId) && part.Type == TranscriptPartTypes.SessionClosed)
            .OrderBy(part => part.Sequence)
            .ThenBy(part => part.Id)
            .ToListAsync(ct);

        var result = new Dictionary<string, TerminalFact>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!sessionByTurnId.TryGetValue(part.TurnId, out var sessionId)) continue;
            var fact = TerminalFact.FromPart(part);
            if (fact is not null) result[sessionId] = fact;
        }
        return result;
    }

    private static TranscriptEventProjection ToProjection(string sessionId, AgentSessionTranscriptPartRow part) => new()
    {
        Id = part.Id,
        SessionId = sessionId,
        Sequence = part.Sequence,
        Type = part.Type,
        PayloadJson = part.Type is "text" or "reasoning"
            ? JSON.Serialize(new { text = part.Text })
            : part.PayloadJson,
        CreatedAt = part.LastSeenAt,
    };

    private static async Task<IReadOnlyList<AgentSessionRecord>> ReconcileActiveSessionsAsync(
        MohistDbContext db,
        IReadOnlyList<AgentSessionRecord> sessions,
        CancellationToken ct)
    {
        if (sessions.Count == 0) return sessions;

        var activeRows = sessions
            .Where(IsActiveSession)
            .ToList();
        if (activeRows.Count == 0) return sessions;

        var runsByWorkflow = await LoadWorkflowRunsForReconciliationAsync(db, activeRows, ct);
        if (runsByWorkflow.Count == 0) return sessions;

        var allowedSessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activeSession in activeRows)
        {
            var workflowRunId = Label(activeSession, AgentSessionQueryMetadataKeys.WorkflowRunId);
            if (workflowRunId is null || !runsByWorkflow.TryGetValue(workflowRunId, out var run) || run is null)
            {
                allowedSessionIds.Add(activeSession.Session.Id);
                continue;
            }

            if (IsSessionAssociatedWithRun(run, activeSession))
                allowedSessionIds.Add(activeSession.Session.Id);
        }

        return sessions
            .Where(s => !IsActiveSession(s) || allowedSessionIds.Contains(s.Session.Id))
            .ToList();
    }

    private static WorkflowRun? DeserializeWorkflowRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, Infrastructure.Data.Sessions.AgentSessionJson.JsonOptions); }
        catch { return null; }
    }

    private static async Task<Dictionary<string, WorkflowRun?>> LoadWorkflowRunsForReconciliationAsync(
        MohistDbContext db, List<AgentSessionRecord> sessions, CancellationToken ct)
    {
        var workflowIds = sessions
            .Select(s => Label(s, AgentSessionQueryMetadataKeys.WorkflowRunId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var rows = await db.WorkflowRuns.AsNoTracking()
            .Where(r => workflowIds.Contains(r.WorkflowRunId))
            .ToListAsync(ct);

        var runs = new Dictionary<string, WorkflowRun?>(StringComparer.Ordinal);
        foreach (var row in rows)
            runs[row.WorkflowRunId] = DeserializeWorkflowRun(row.State);
        return runs;
    }

    /// <summary>
    /// Determines whether <paramref name="session"/> is associated with <paramref name="run"/>
    /// by reference through a <see cref="TaskRun"/>, not by ownership.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentSession"/> is a peer aggregate, never owned by <see cref="WorkflowRun"/>.
    /// The association is established through a <see cref="TaskRun"/> session reference and
    /// relies on the single-runner assignment invariant: if the run is assigned, the session MUST
    /// belong to the same runner (<see cref="WorkflowAssignmentInfo.RunnerId"/> == session.RunnerId)
    /// and the task identified by <see cref="AgentSessionQueryMetadataKeys.WorkId"/> (if running)
    /// MUST match the session's work item (the task whose reference links them).
    /// When the run has no assignment yet (<see cref="WorkflowRun.AssignedTo"/> is null), any active
    /// session known by workflow-run-id is provisionally accepted.
    /// </remarks>
    private static bool IsSessionAssociatedWithRun(WorkflowRun run, AgentSessionRecord session)
    {
        if (run.AssignedTo is null) return true;

        if (!string.Equals(run.AssignedTo, session.Row.RunnerId, StringComparison.Ordinal))
            return false;

        var runningTask = run.Stages
            .SelectMany(s => s.Tasks)
            .FirstOrDefault(t => t.Status == Workflow.Domain.Run.TaskRunStatus.Running);

        return runningTask is null || string.Equals(runningTask.Id, Label(session, AgentSessionQueryMetadataKeys.WorkId), StringComparison.Ordinal);
    }

    private static bool IsActiveSession(AgentSessionRecord session) =>
        session.Row.AgentSessionId is not null;
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
    bool IsActive);

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
