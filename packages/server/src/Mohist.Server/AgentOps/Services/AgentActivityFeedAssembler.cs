using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.AgentOps.Services;

/// <summary>
/// Assembles the activity-feed payload for the
/// <c>GET /api/projects/{projectRef}/agent/activity</c> endpoint
///. Composes listing + active-session
/// reconciliation + latest-event / event-summary / issue-title /
/// task-progress / preview-card projection into a single
/// <see cref="ActivityDto"/>. Depends one-way on the shared
/// <see cref="ActiveSessionReconciler.ReconcileAsync"/> and
/// <see cref="TranscriptReductions.LoadEventSummariesAsync"/> reductions
/// and the shared <see cref="TranscriptPartLoader"/> — the core
/// querier does not depend on this service.
/// </summary>
/// <remarks>
/// Previously these methods (and their private helpers
/// <c>ToActivityCard</c>, <c>BuildTaskProgressMapAsync</c>,
/// <c>ToPreview</c>, <c>ExtractPreviewText</c>,
/// <c>Truncate</c>, <c>LoadLatestEventsAsync</c>) lived
/// on the core <see cref="AgentSessionQuerier"/> together with five
/// unrelated concerns. Splitting the activity-feed projection out keeps
/// the core querier a pure query service and gives the assembly logic a
/// navigable home of its own. The
/// <see cref="Mohist.Server.Workflow.Services.WorkflowQuerier"/>
/// dependency moved from the core querier here (it was only consumed by
/// the task-progress map) and the core querier constructor signature
/// dropped that argument. The issue-title batch lookup and the
/// <c>Issue #{number}</c> fallback resolver moved to
/// <see cref="IssueTitleLookup"/> on the Issue read side
///, so the assembler now reads the
/// same <c>(project, numbers)</c> tuple as
/// <see cref="AgentSessionListAssembler.ListCurrentAsync"/>.
/// </remarks>
public sealed class AgentActivityFeedAssembler : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentSessionQuery _sessionQuery;
    private readonly Mohist.Server.Workflow.Services.WorkflowQuerier _workflowQuerier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentActivityFeedAssembler> _logger;

    public AgentActivityFeedAssembler(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentSessionQuery sessionQuery,
        Mohist.Server.Workflow.Services.WorkflowQuerier workflowQuerier,
        TimeProvider timeProvider,
        ILogger<AgentActivityFeedAssembler> logger)
    {
        _dbFactory = dbFactory;
        _sessionQuery = sessionQuery;
        _workflowQuerier = workflowQuerier;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Builds the activity-feed payload for a project: the summary
    /// counters (active / waiting / completed / failed / slot usage),
    /// one <see cref="ActivityCardDto"/> per session (with usage /
    /// event-summary / work-item / task-progress / last-activity
    /// projections), and the waiting cards passed in by the route.
    /// </summary>
    public async Task<ActivityDto> GetActivityAsync(
        string projectId,
        int? limit = null,
        IReadOnlyList<ActivityWaitingCardDto>? waiting = null,
        RunnerCapacityView? capacity = null,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await _sessionQuery.ListByLabelsAsync(
                AgentSessionDtoMapper.Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
                AgentSessionQueryOrder.CreatedDescending,
                take,
                ct: ct);
        var candidatesCount = candidates.Count;
        var sessions = await ActiveSessionReconciler.ReconcileAsync(db, candidates, _logger, ct);

        var sessionIds = sessions.Select(s => s.Session.Id).ToArray();
        var latestEventsLoad = await LoadLatestEventsAsync(db, sessionIds, ct);
        var eventSummariesLoad = await TranscriptReductions.LoadEventSummariesWithCountAsync(db, sessionIds, ct);
        var issueTitles = await IssueTitleLookup.LoadTitlesAsync(db, projectId, sessions.Select(r => r.IssueNumber()), ct);
        var taskProgressMap = await BuildTaskProgressMapAsync(sessions, ct);

        var cards = sessions
            .Select(record => ToActivityCard(
                record,
                latestEventsLoad.Projections.GetValueOrDefault(record.Session.Id),
                eventSummariesLoad.Summaries.GetValueOrDefault(record.Session.Id),
                IssueTitleLookup.Resolve(issueTitles, record.IssueNumber()),
                taskProgressMap.GetValueOrDefault(record.Session.Id)))
            .ToList();

        waiting ??= [];
        var slots = new ActivitySlotUsageDto(capacity?.UsedSlots ?? 0, capacity?.TotalSlots ?? 0);
        var summary = new ActivitySummaryDto(
            cards.Count(c => c.Status == "active"),
            waiting.Count,
            0,
            0,
            slots);

        var amplification = new AgentAmplificationDto(
            Candidates: candidatesCount,
            Processed: cards.Count,
            TranscriptRecords: latestEventsLoad.TranscriptRecords + eventSummariesLoad.TranscriptRecords,
            DatabaseCalls: 0,
            DownstreamCalls: 0);

        return new ActivityDto(summary, cards, waiting.ToList(), amplification);
    }

    /// <summary>
    /// Builds the per-session <c>completed/total</c> task-progress
    /// projection by resolving each session's workflow-run status, finding
    /// its current stage, and counting completed tasks within that stage.
    /// Sessions without a <c>workflow-run-id</c> label, without a current
    /// stage, or whose workflow run does not exist contribute nothing to
    /// the map (the activity card renderer treats a missing entry as "no
    /// progress to show").
    /// </summary>
    private async Task<Dictionary<string, ActivityTaskProgressDto>> BuildTaskProgressMapAsync(
        IReadOnlyList<AgentSessionRecord> sessions,
        CancellationToken ct)
    {
        var result = new Dictionary<string, ActivityTaskProgressDto>(StringComparer.Ordinal);
        var workflowRunIds = sessions
            .Select(s => s.Label(AgentSessionQueryMetadataKeys.WorkflowRunId))
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
            var workflowRunId = session.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
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

    /// <summary>
    /// Projects a single session into its <see cref="ActivityCardDto"/>.
    /// The shape diverges by source-kind: <c>agent-launch</c> sessions
    /// carry agent id/name; workflow sessions carry no agent attribution.
    /// The two branches agree on every other
    /// field (status, model, timestamps, work-item, task-progress, last
    /// activity preview, event-summary, usage). Status, usage and
    /// event-summary projections are sourced from the shared
    /// <see cref="AgentSessionDtoMapper"/> so list / summary / activity
    /// feeds stay in lockstep.
    /// </summary>
    private ActivityCardDto ToActivityCard(
        AgentSessionRecord record,
        TranscriptEventProjection? latestEvent,
        AgentSessionTranscriptSummary? eventSummary,
        string issueTitle,
        ActivityTaskProgressDto? taskProgress)
    {
        var s = record.Session;
        var lastActivityAt = AgentSessionJsonHelper.LastActivityAt(s).ToString("o");
        var issueNumber = record.IssueNumber();
        var sessionName = record.Label(AgentSessionQueryMetadataKeys.SessionName) ?? s.Id;
        var stage = record.Label(AgentSessionQueryMetadataKeys.Stage);
        var workId = record.Label(AgentSessionQueryMetadataKeys.WorkId);
        var workType = record.Label(AgentSessionQueryMetadataKeys.WorkType);
        var sourceKind = record.Label(AgentSessionQueryMetadataKeys.SourceKind);

        if (string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal))
        {
            var agentId = record.Label(GenericAgentSessionMetadata.AgentId) ?? string.Empty;
            var agentName = record.Label(GenericAgentSessionMetadata.AgentName) ?? string.Empty;
            return new ActivityCardDto(
                issueNumber,
                issueTitle,
                stage ?? string.Empty,
                null,
                s.Id,
                AgentSessionJsonHelper.ActivityName(s),
                s.Settings.Model,
                null,
                s.Status.CreatedAt.ToString("o"),
                null,
                lastActivityAt,
                new ActivityWorkItemDto(string.IsNullOrEmpty(workType) ? "task" : workType, workId ?? sessionName, workId ?? sessionName, stage, null),
                taskProgress,
                latestEvent is null ? null : ToPreview(latestEvent),
                null,
                agentId,
                agentName,
                AgentSessionDtoMapper.ToEventSummaryDto(eventSummary),
                AgentSessionDtoMapper.ToUsageDto(s));
        }

        return new ActivityCardDto(
            issueNumber,
            issueTitle,
            stage ?? string.Empty,
            null,
            s.Id,
            AgentSessionJsonHelper.ActivityName(s),
            s.Settings.Model,
            null,
            s.Status.CreatedAt.ToString("o"),
            null,
            lastActivityAt,
            new ActivityWorkItemDto(string.IsNullOrEmpty(workType) ? "task" : workType, workId ?? sessionName, workId ?? sessionName, stage, null),
            taskProgress,
            latestEvent is null ? null : ToPreview(latestEvent),
            null,
            null,
            null,
            AgentSessionDtoMapper.ToEventSummaryDto(eventSummary),
            AgentSessionDtoMapper.ToUsageDto(s));
    }

    /// <summary>
    /// Loads the latest transcript event per session id, ordered by
    /// <c>LastSeenAt, Id</c> (the same last-wins-by-LastSeenAt semantics
    /// the pre-split core querier used). Returns an empty dictionary when
    /// no sessions are requested or no parts exist.
    /// </summary>
    private static async Task<TranscriptProjectionLoad> LoadLatestEventsAsync(
        MohistDbContext db,
        string[] sessionIds,
        CancellationToken ct)
    {
        if (sessionIds.Length == 0) return new([], 0);

        var loaded = await TranscriptPartLoader.LoadAsync(db, sessionIds, ct: ct);
        if (loaded.Parts.Count == 0) return new([], 0);

        var result = new Dictionary<string, TranscriptEventProjection>(StringComparer.Ordinal);
        foreach (var part in loaded.Parts.OrderBy(e => e.LastSeenAt).ThenBy(e => e.Id))
            if (loaded.SessionByTurnId.TryGetValue(part.TurnId, out var sessionId))
                result[sessionId] = AgentSessionDtoMapper.ToProjection(sessionId, part);

        return new(result, loaded.Parts.Count);
    }

    private static ActivityPreviewDto ToPreview(TranscriptEventProjection e)
    {
        var text = ExtractPreviewText(e.PayloadJson);
        var kind = e.Type.Contains("tool", StringComparison.OrdinalIgnoreCase) ? "tool" : "text";
        return new ActivityPreviewDto(
            kind,
            string.IsNullOrWhiteSpace(text) ? e.Type : Truncate(text, 120),
            e.CreatedAt.ToString("o"));
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

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private readonly record struct TranscriptProjectionLoad(
        Dictionary<string, TranscriptEventProjection> Projections,
        long TranscriptRecords);
}
