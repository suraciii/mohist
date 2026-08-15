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
public partial class AgentSessionQuerier : IScopedService
{
    private const string AgentLaunchSourceKind = "agent-launch";
    private const string AgentConnectionSourceKind = "agent-connection";

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
        var transcript = await LoadTranscriptAsync(db, session.Session.Id, session.Session.Status.AgentRuntimeSessionId, ct);
        return new WorkflowSessionDetailDto(ToWorkflowDto(session), SessionTranscriptBuilder.Build(transcript, session.Session));
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
        var records = await ListAgentSessionRecordsAsync(
            projectId,
            agentId,
            [AgentLaunchSourceKind],
            clampedLimit,
            additionalContextLabels,
            ct);

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
                BuildAgentSessionListContextRefs(record),
                record.Label(GenericAgentSessionMetadata.Origin),
                record.Label(GenericAgentSessionMetadata.TargetId));
        }).ToList();

        if (statusSet is { Count: > 0 })
        {
            var set = new HashSet<string>(statusSet, StringComparer.OrdinalIgnoreCase);
            items = items.Where(i => set.Contains(i.Activity)).ToList();
        }

        return items;
    }

    /// <summary>
    /// Lists an Agent's sessions for the source-agnostic session list. Agent
    /// Connection sessions share the Agent identity and session read contract
    /// with direct launches, but remain distinct from Workflow sessions.
    /// </summary>
    public async Task<IReadOnlyList<UnifiedSessionListItemDto>> ListUnifiedSessionsByAgentAsync(
        string projectId,
        string agentId,
        int limit = 50,
        CancellationToken ct = default)
    {
        var records = await ListAgentSessionRecordsAsync(
            projectId,
            agentId,
            [AgentLaunchSourceKind, AgentConnectionSourceKind],
            limit,
            additionalContextLabels: null,
            ct: ct);
        if (records.Count == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var eventSummaries = await TranscriptReductions.LoadEventSummariesAsync(
            db,
            records.Select(record => record.Session.Id).ToArray(),
            ct);

        return records.Select(record =>
        {
            var session = record.Session;
            var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind)!;
            var summary = eventSummaries.GetValueOrDefault(session.Id);
            return new UnifiedSessionListItemDto(
                Id: session.Id,
                Source: sourceKind,
                RuntimeSessionId: session.Status.AgentRuntimeSessionId,
                Runtime: session.Runtime.Runtime,
                Activity: ResolveAgentSessionActivity(record),
                CreatedAt: session.Status.CreatedAt.ToString("o"),
                LastActivityAt: AgentSessionJsonHelper.LastActivityAt(session).ToString("o"),
                Model: summary?.ResolvedModel ?? session.Settings.Model,
                AgentId: record.Label(GenericAgentSessionMetadata.AgentId),
                AgentName: record.Label(GenericAgentSessionMetadata.AgentName),
                WorkflowRunId: null,
                SessionName: null,
                ContextRefs: BuildUnifiedContextRefs(record),
                Origin: record.Label(GenericAgentSessionMetadata.Origin),
                TargetId: record.Label(GenericAgentSessionMetadata.TargetId));
        }).ToList();
    }

    /// <summary>
    /// Lists sessions bound to a named Workspace for the source-agnostic
    /// session list and the workspace view. The workspace-name label is
    /// optional on sessions, so the query matches only sessions that
    /// explicitly bound the workspace at launch.
    /// </summary>
    public async Task<IReadOnlyList<UnifiedSessionListItemDto>> ListUnifiedSessionsByWorkspaceAsync(
        string projectId,
        string workspaceName,
        int limit = 100,
        CancellationToken ct = default)
    {
        var records = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.WorkspaceName, workspaceName)),
            AgentSessionQueryOrder.CreatedDescending,
            limit: limit,
            ct: ct);
        if (records.Count == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var eventSummaries = await TranscriptReductions.LoadEventSummariesAsync(
            db,
            records.Select(record => record.Session.Id).ToArray(),
            ct);

        return records.Select(record =>
        {
            var session = record.Session;
            var summary = eventSummaries.GetValueOrDefault(session.Id);
            return new UnifiedSessionListItemDto(
                Id: session.Id,
                Source: record.Label(AgentSessionQueryMetadataKeys.SourceKind) ?? "agent-launch",
                RuntimeSessionId: session.Status.AgentRuntimeSessionId,
                Runtime: session.Runtime.Runtime,
                Activity: ResolveAgentSessionActivity(record),
                CreatedAt: session.Status.CreatedAt.ToString("o"),
                LastActivityAt: AgentSessionJsonHelper.LastActivityAt(session).ToString("o"),
                Model: summary?.ResolvedModel ?? session.Settings.Model,
                AgentId: record.Label(GenericAgentSessionMetadata.AgentId),
                AgentName: record.Label(GenericAgentSessionMetadata.AgentName),
                WorkflowRunId: null,
                SessionName: null,
                ContextRefs: BuildUnifiedContextRefs(record),
                Origin: record.Label(GenericAgentSessionMetadata.Origin),
                TargetId: record.Label(GenericAgentSessionMetadata.TargetId));
        }).ToList();
    }

    private async Task<IReadOnlyList<AgentSessionRecord>> ListAgentSessionRecordsAsync(
        string projectId,
        string agentId,
        IReadOnlyList<string> sourceKinds,
        int limit,
        IReadOnlyDictionary<string, string>? additionalContextLabels,
        CancellationToken ct)
    {
        var clampedLimit = Math.Clamp(limit, 1, 200);
        var records = new List<AgentSessionRecord>();
        foreach (var sourceKind in sourceKinds)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [GenericAgentSessionMetadata.AgentId] = agentId,
                [AgentSessionQueryMetadataKeys.SourceKind] = sourceKind,
            };
            if (additionalContextLabels is not null)
            {
                foreach (var (key, value) in additionalContextLabels)
                {
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                        labels[key] = value;
                }
            }

            records.AddRange(await _sessionQuery.ListByLabelsAsync(
                labels,
                AgentSessionQueryOrder.CreatedDescending,
                clampedLimit,
                ct: ct));
        }

        return records
            .OrderByDescending(record => record.Session.Status.CreatedAt)
            .ThenByDescending(record => record.Session.Id, StringComparer.Ordinal)
            .Take(clampedLimit)
            .ToList();
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
            : new AgentSessionListContextRefsDto(refs.Value.IssueNumber, refs.Value.EpicNumber, refs.Value.Repository, refs.Value.WorkspaceName);
    }

    public async Task<string?> ResolveIssueSessionIdAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        return record?.Session.Id;
    }

    /// <summary>
    /// Resolves the single AgentSession bound to a Slack (connection,
    /// conversation, thread) provenance. Used by the channel ingress to
    /// repair a missing thread-binding row after a launch crashed
    /// between <c>LaunchConnectionAsync</c> and <c>BindAsync</c>: the
    /// session is already persisted with these labels, only the
    /// mapping row is gone. Returns null when no matching session exists.
    /// </summary>
    public async Task<string?> FindSessionIdBySlackThreadProvenanceAsync(
        string projectId,
        string connectionId,
        string conversationId,
        string threadTs,
        CancellationToken ct = default)
    {
        var record = await _sessionQuery.FirstByLabelsAsync(
            AgentSessionDtoMapper.Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.ConnectionId, connectionId),
                (AgentSessionQueryMetadataKeys.SlackConversationId, conversationId),
                (AgentSessionQueryMetadataKeys.SlackThreadTs, threadTs)),
            ct: ct);
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
            record.Label(GenericAgentSessionMetadata.AgentId));
    }

    public async Task<SessionStopTarget?> ResolveStopTargetAsync(string projectId, string sessionId, CancellationToken ct = default)
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
            && !string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal)
            && !string.Equals(sourceKind, "agent-connection", StringComparison.Ordinal))
            return null;

        return new SessionStopTarget(
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
        CancellationToken ct = default,
        string? view = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindReadableSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var transcript = await LoadTranscriptAsync(
            db,
            session.Session.Id,
            runtimeSessionId ?? session.Session.Status.AgentRuntimeSessionId,
            ct);
        return SessionTranscriptBuilder.Build(transcript, session.Session, view);
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
            CurrentTurnId(session),
            AgentSessionObservationMapper.Inputs(session.Status),
            AgentSessionObservationMapper.Turns(session.Status),
            Interruption: AgentSessionObservationMapper.Current(session.Status),
            InterruptionHistory: AgentSessionObservationMapper.History(session.Status),
            Origin: record.Label(GenericAgentSessionMetadata.Origin),
            TargetId: record.Label(GenericAgentSessionMetadata.TargetId));
    }

    private static bool IsApplicableToCurrentRuntime(
        TranscriptEventProjection projection,
        IReadOnlyDictionary<long, long> turnSequenceByTurnId,
        string? currentRuntimeSessionId,
        TranscriptPartLoaderResult loaded)
    {
        _ = turnSequenceByTurnId;
        var turn = loaded.Turns.FirstOrDefault(t => t.Id == projection.TurnId);
        return turn is not null && MatchesRuntimeSession(turn.RuntimeSessionId, currentRuntimeSessionId);
    }

    private static bool MatchesRuntimeSession(string? turnRuntimeSessionId, string? currentRuntimeSessionId) =>
        string.IsNullOrWhiteSpace(currentRuntimeSessionId)
            ? string.IsNullOrWhiteSpace(turnRuntimeSessionId)
            : string.Equals(turnRuntimeSessionId, currentRuntimeSessionId, StringComparison.Ordinal);

    public async Task<AgentSessionTranscriptResponse?> GetGenericSessionTranscriptAsync(
        string projectId,
        string sessionId,
        string? runtimeSessionId = null,
        CancellationToken ct = default,
        string? view = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindGenericSessionAsync(projectId, sessionId, ct);
        if (session is null) return null;

        var transcript = await LoadTranscriptAsync(
            db,
            session.Session.Id,
            runtimeSessionId ?? session.Session.Status.AgentRuntimeSessionId,
            ct);
        return SessionTranscriptBuilder.Build(transcript, session.Session, view);
    }

    /// <summary>
    /// Builds the unified source-agnostic session summary surfaced by
    /// <c>GET /api/projects/{projectRef}/sessions/{sessionId}</c>.
    /// Resolves the row by id WITHOUT the
    /// <c>source-kind == agent-launch</c> gate applied by
    /// <see cref="FindGenericSessionAsync"/> — a workflow-originated session
    /// resolves here by the same stable id as an agent-launch session. The
    /// cross-project guard matches <see cref="ResolveCanonicalFollowupTargetAsync"/>
    /// so the caller never observes a session from a different project. The
    /// DTO carries every fact the Web session detail page consumes — source
    /// and current state, current-turn and input/turn observations,
    /// terminal/failure evidence, model/usage, recovery availability, and
    /// the runtime binding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The summary branches internally on the resolved
    /// <see cref="AgentSessionQueryMetadataKeys.SourceKind"/> to populate
    /// source-specific identity only for its source: agent-launch and
    /// agent-connection sessions carry <c>agentId</c> / <c>agentName</c>;
    /// workflow sessions carry <c>workflowRunId</c> / <c>sessionName</c>.
    /// The absent-when-empty idiom
    /// (<see cref="Infrastructure.JSON.Options"/>) omits the unused branch's
    /// fields from the wire rather than nulling them.
    /// </para>
    /// <para>
    /// Failure evidence (category, reason, tool counts) and the resolved
    /// model are computed from the session's transcript scoped to the
    /// current <see cref="AgentSessionStatusSnapshot.AgentRuntimeSessionId"/>
    /// binding so a prior Runtime Session's terminal facts do not leak into
    /// the current view. The same total order
    /// (<c>(turn sequence, part sequence, part id)</c>) used by
    /// <see cref="GetGenericSessionSummaryAsync"/> is preserved.
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
        var isAgentSession = string.Equals(sourceKind, AgentLaunchSourceKind, StringComparison.Ordinal)
            || string.Equals(sourceKind, AgentConnectionSourceKind, StringComparison.Ordinal);
        if (!isWorkflow && !isAgentSession) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var transcriptSummary = await ResolveTranscriptSummaryAsync(db, session.Id, session, ct);

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
            ResolvedModel: transcriptSummary.ResolvedModel,
            FailureCategory: transcriptSummary.FailureCategory,
            FailureReason: transcriptSummary.FailureReason,
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
            Interruption: AgentSessionObservationMapper.Current(session.Status),
            InterruptionHistory: AgentSessionObservationMapper.History(session.Status),
            Origin: record.Label(GenericAgentSessionMetadata.Origin),
            TargetId: record.Label(GenericAgentSessionMetadata.TargetId));
    }

    /// <summary>
    /// Builds the unified source-agnostic transcript surfaced by
    /// <c>GET /api/projects/{projectRef}/sessions/{sessionId}/transcript</c>.
    /// Resolves the row by id WITHOUT the
    /// source gate, so a workflow-originated or Agent Connection session's
    /// transcript resolves here by the same stable id as an agent-launch
    /// session's. Returns <c>null</c> for an unknown id, a
    /// cross-project session, or an unknown source kind.
    /// </summary>
    public async Task<AgentSessionTranscriptResponse?> GetUnifiedSessionTranscriptAsync(
        string projectId,
        string sessionId,
        string? runtimeSessionId = null,
        CancellationToken ct = default,
        string? view = null)
    {
        var record = await FindUnifiedSessionAsync(projectId, sessionId, ct);
        if (record is null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var transcript = await LoadTranscriptAsync(
            db,
            record.Session.Id,
            runtimeSessionId ?? record.Session.Status.AgentRuntimeSessionId,
            ct);
        return SessionTranscriptBuilder.Build(transcript, record.Session, view);
    }

    /// <summary>
    /// Resolves a session row by id without the <c>source-kind</c> gate and
    /// enforces project isolation. Returns <c>null</c> when the id does not
    /// resolve, the session belongs to a different project, or the source
    /// kind is not one of the supported Agent launch, Agent Connection, or
    /// Workflow sources.
    /// </summary>
    private async Task<AgentSessionRecord?> FindUnifiedSessionAsync(string projectId, string sessionId, CancellationToken ct)
    {
        var records = await _sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null) return null;

        if (!string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal))
            return null;

        var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind);
        if (!string.Equals(sourceKind, AgentLaunchSourceKind, StringComparison.Ordinal)
            && !string.Equals(sourceKind, AgentConnectionSourceKind, StringComparison.Ordinal)
            && !string.Equals(sourceKind, "workflow", StringComparison.Ordinal))
            return null;

        return record;
    }

    /// <summary>
    /// Resolves the full transcript summary for the unified session read —
    /// resolved model, failure category/reason, and tool call/error counts
    /// — scoped to the session's current
    /// <see cref="AgentSessionStatusSnapshot.AgentRuntimeSessionId"/>
    /// binding so prior-runtime facts do not leak into the current view.
    /// For sessions with a transcript-persisted resolved model, prefers the
    /// latest transcript <c>model</c> event; falls back to the session's
    /// declared <see cref="AgentSessionSettings.Model"/> when no
    /// transcript-persisted resolved model exists, so the unified read
    /// surfaces a model name even on a freshly-opened session.
    /// </summary>
    private async Task<UnifiedTranscriptSummary> ResolveTranscriptSummaryAsync(
        MohistDbContext db,
        string sessionId,
        AgentSession session,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, new[] { sessionId }, ct: ct);
        var projections = ToTranscriptProjectionsInSequenceOrder(loaded);
        var turnSequenceByTurnId = loaded.Turns.ToDictionary(t => t.Id, t => t.Sequence);
        var runtimeSessionId = session.Status.AgentRuntimeSessionId;
        var summary = TranscriptEventSummaryProjector.Summarize(
            projections
                .Where(e => IsApplicableToCurrentRuntime(e, turnSequenceByTurnId, runtimeSessionId, loaded))
                .Select(e => new TranscriptSummaryEvent(
                    TurnSequence: turnSequenceByTurnId.GetValueOrDefault(e.TurnId, 0),
                    Sequence: e.Sequence,
                    PartId: e.Id.ToString(),
                    Type: e.Type,
                    PayloadJson: e.PayloadJson)));
        return new UnifiedTranscriptSummary(
            summary.ResolvedModel ?? session.Settings.Model,
            summary.FailureCategory,
            summary.FailureReason,
            summary.ToolCallCount,
            summary.ToolErrorCount,
            BuildRecoveryHistory(loaded));
    }

    private static IReadOnlyList<AgentSessionRecoveryObservationDto>? BuildRecoveryHistory(
        TranscriptPartLoaderResult loaded)
    {
        var turnById = loaded.Turns.ToDictionary(turn => turn.Id);
        var recoveryParts = loaded.Parts
            .Where(part => part.Type is TranscriptPartTypes.SessionContextReset
                or TranscriptPartTypes.Compaction
                or RuntimeEventTypes.CompactionEvent)
            .OrderBy(part => turnById.TryGetValue(part.TurnId, out var turn) ? turn.Sequence : long.MaxValue)
            .ThenBy(part => part.Sequence)
            .ThenBy(part => part.Id)
            .ToList();
        if (recoveryParts.Count == 0) return null;

        var seenCompactions = new HashSet<string>(StringComparer.Ordinal);
        var history = new List<AgentSessionRecoveryObservationDto>(recoveryParts.Count);
        foreach (var part in recoveryParts)
        {
            var payload = AgentSessionJsonHelper.ParsePayloadOrEmpty(part.PayloadJson);
            var isReset = part.Type == TranscriptPartTypes.SessionContextReset;
            if (!isReset && !seenCompactions.Add(part.PayloadJson)) continue;

            history.Add(new AgentSessionRecoveryObservationDto(
                Type: isReset ? "reset" : "compaction",
                RecordedAt: AgentSessionJsonHelper.GetStringProp(payload, "recordedAt")
                    ?? AgentSessionJsonHelper.GetStringProp(payload, "observedAt")
                    ?? part.FirstSeenAt.ToString("o"),
                RuntimeSessionId: turnById.GetValueOrDefault(part.TurnId)?.RuntimeSessionId,
                Reason: isReset ? AgentSessionJsonHelper.GetStringProp(payload, "reason") : null,
                Strategy: isReset ? null : AgentSessionJsonHelper.GetStringProp(payload, "strategy"),
                Summary: isReset ? null : AgentSessionJsonHelper.GetStringProp(payload, "summary"),
                ContextWindowUsedBefore: isReset ? null : AgentSessionJsonHelper.GetLongProp(payload, "contextWindowUsedBefore"),
                ContextWindowUsedAfter: isReset ? null : AgentSessionJsonHelper.GetLongProp(payload, "contextWindowUsedAfter"),
                ContextWindowSize: isReset ? null : AgentSessionJsonHelper.GetLongProp(payload, "contextWindowSize")));
        }

        return history.ToArray();
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
        var workspaceName = agentRefs?.WorkspaceName;

        if (issueNumber is null && epicNumber is null
            && string.IsNullOrWhiteSpace(repository)
            && string.IsNullOrWhiteSpace(workspaceName))
            return null;

        return new UnifiedSessionContextRefsDto(issueNumber, epicNumber, repository, workspaceName);
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
            : new GenericAgentSessionSummaryContextRefsDto(refs.Value.IssueNumber, refs.Value.EpicNumber, refs.Value.Repository, refs.Value.WorkspaceName);
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
            CurrentTurnId(domainSession),
            AgentSessionObservationMapper.Inputs(domainSession.Status),
            AgentSessionObservationMapper.Turns(domainSession.Status),
            AgentSessionObservationMapper.Current(domainSession.Status),
            AgentSessionObservationMapper.History(domainSession.Status));
    }

    private static string? CurrentTurnId(AgentSession session) =>
        session.Status.Turns?.FirstOrDefault(turn => turn.Status == AgentTurnStatus.Executing)?.Id
        ?? session.Status.Turns?.LastOrDefault(turn => turn.Status == AgentTurnStatus.Queued)?.Id;

    private static bool IsRecoveryAvailable(AgentSession session) =>
        session.Status.Activity == AgentSessionActivity.Idle
        && session.Status.PendingReset is null
        && session.Status.PendingStop is not { IsActive: true }
        && session.Status.PendingFollowup is null
        && (session.Status.PendingFollowups is null || session.Status.PendingFollowups.Count == 0)
        && !(session.Status.Turns ?? [])
            .Any(turn => turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);

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
        AgentSessionDtoMapper.ToUsageDto(s),
        AgentSessionObservationMapper.Current(s.Status),
        AgentSessionObservationMapper.History(s.Status));
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
            record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId),
            AgentSessionObservationMapper.Current(s.Status),
            AgentSessionObservationMapper.History(s.Status));
    }

    private static async Task<AgentSessionTranscriptData> LoadTranscriptAsync(
        MohistDbContext db,
        string sessionId,
        string? runtimeSessionId,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, new[] { sessionId }, ct: ct);
        var turns = loaded.Turns
            .Where(turn => MatchesRuntimeSession(turn.RuntimeSessionId, runtimeSessionId))
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

    private static IReadOnlyList<TranscriptEventProjection> ToTranscriptProjectionsInSequenceOrder(TranscriptPartLoaderResult loaded)
    {
        var turnSequenceByTurnId = loaded.Turns.ToDictionary(turn => turn.Id, turn => turn.Sequence);
        return loaded.Parts
            .Where(part => loaded.SessionByTurnId.ContainsKey(part.TurnId))
            .OrderBy(part => turnSequenceByTurnId.GetValueOrDefault(part.TurnId, long.MaxValue))
            .ThenBy(part => part.Sequence)
            .ThenBy(part => part.Id)
            .Select(part => AgentSessionDtoMapper.ToProjection(loaded.SessionByTurnId[part.TurnId], part))
            .ToList();
    }
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

internal sealed record UnifiedTranscriptSummary(
    string? ResolvedModel,
    string? FailureCategory,
    string? FailureReason,
    int? ToolCallCount,
    int? ToolErrorCount,
    IReadOnlyList<AgentSessionRecoveryObservationDto>? RecoveryHistory);
