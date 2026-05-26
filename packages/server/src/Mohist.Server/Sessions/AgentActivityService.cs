using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Projection;

namespace Mohist.Server.Sessions;

public class AgentActivityService
{
    private const int DefaultLimit = 50;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueQueryService _issues;
    private readonly WorkflowProjectionService _workflowProjection;
    private readonly IGrainFactory _grains;

    public AgentActivityService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueQueryService issues,
        WorkflowProjectionService workflowProjection,
        IGrainFactory grains)
    {
        _dbFactory = dbFactory;
        _issues = issues;
        _workflowProjection = workflowProjection;
        _grains = grains;
    }

    public async Task<AgentActivityDto> GetAsync(string projectId, int? limit = null, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = await db.AgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        var issues = (await _issues.ListAsync(projectId, all: true))
            .ToDictionary(i => i.Number);

        var activeAgents = await _workflowProjection.ListActiveAgentsAsync(projectId, ct);
        var activeBySessionId = activeAgents.ToDictionary(a => a.SessionId);
        var latestTranscriptEntries = await LoadLatestTranscriptEntriesAsync(db, sessions.Select(s => s.Id).ToArray(), ct);
        var cards = sessions
            .Select(s => ToCard(s, issues.GetValueOrDefault(s.IssueNumber), activeBySessionId.GetValueOrDefault(s.Id), latestTranscriptEntries.GetValueOrDefault(s.Id)))
            .ToList();

        var waiting = issues.Values
            .Where(i => i.StageApproval?.Status == "awaiting")
            .Select(i => new AgentActivityWaitingCardDto(
                i.Id,
                i.Number,
                i.Title,
                i.StageApproval?.Stage,
                "Needs Approval",
                i.StageApproval?.RequestedAt,
                i.StageApproval?.OutputJson))
            .OrderByDescending(c => c.RequestedAt)
            .ToList();

        var registry = _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        var runnerIds = await registry.ListRunnerIdsAsync();
        var summary = new AgentActivitySummaryDto(
            cards.Count(c => c.Status is "created" or "running" or "probing"),
            waiting.Count,
            cards.Count(c => c.Status == "completed"),
            cards.Count(c => c.Status is "failed" or "cancelled"),
            new AgentActivitySlotUsageDto(activeAgents.Count, runnerIds.Count));

        return new AgentActivityDto(summary, cards, waiting);
    }

    private static AgentActivityCardDto ToCard(
        AgentSessionRecord session,
        IssueReadModel? issue,
        ActiveAgentDto? active,
        AgentSessionTranscriptEntry? latestTranscriptEntry)
    {
        var lastActivityAt = (session.LastDataAt ?? session.StartedAt ?? session.CreatedAt).ToString("o");
        var progress = active?.Progress;
        var currentWork = progress?.CurrentWorkItem is null
            ? new AgentActivityWorkItemDto(session.WorkType, session.WorkId, session.Title ?? session.WorkId, session.Stage, null)
            : new AgentActivityWorkItemDto(
                progress.CurrentWorkItem.Type,
                progress.CurrentWorkItem.Id,
                progress.CurrentWorkItem.Title,
                progress.Stage,
                session.WorkType);

        var taskProgress = progress?.TaskProgress is null
            ? null
            : new AgentActivityTaskProgressDto(progress.TaskProgress.Completed, progress.TaskProgress.Total);

        return new AgentActivityCardDto(
            issue?.Id ?? $"issue_{session.ProjectId}_{session.IssueNumber}",
            session.IssueNumber,
            issue?.Title ?? $"Issue #{session.IssueNumber}",
            issue?.Stage ?? session.Stage ?? "",
            issue?.RuntimeStatus,
            session.Id,
            session.Status,
            session.Model,
            session.Title,
            session.CreatedAt.ToString("o"),
            session.CompletedAt?.ToString("o"),
            lastActivityAt,
            currentWork,
            taskProgress,
            latestTranscriptEntry is null ? null : ToPreview(latestTranscriptEntry),
            session.FailureReason);
    }

    private static async Task<Dictionary<string, AgentSessionTranscriptEntry>> LoadLatestTranscriptEntriesAsync(
        MohistDbContext db,
        string[] sessionIds,
        CancellationToken ct)
    {
        if (sessionIds.Length == 0) return [];

        var latestIds = await db.AgentSessionTranscriptEntries.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId))
            .GroupBy(e => e.SessionId)
            .Select(g => new { SessionId = g.Key, Sequence = g.Max(e => e.Sequence) })
            .ToListAsync(ct);
        if (latestIds.Count == 0) return [];

        var latestBySession = latestIds.ToDictionary(e => e.SessionId, e => e.Sequence);
        var transcriptEntries = await db.AgentSessionTranscriptEntries.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId))
            .ToListAsync(ct);

        return transcriptEntries
            .Where(e => latestBySession.TryGetValue(e.SessionId, out var sequence) && e.Sequence == sequence)
            .ToDictionary(e => e.SessionId);
    }

    private static AgentActivityPreviewDto ToPreview(AgentSessionTranscriptEntry e)
    {
        var text = ExtractPreviewText(e.PayloadJson);
        var kind = e.Type.Contains("tool", StringComparison.OrdinalIgnoreCase) ? "tool" : "text";
        return new AgentActivityPreviewDto(kind, string.IsNullOrWhiteSpace(text) ? e.Type : Truncate(text, 120), e.CreatedAt.ToString("o"));
    }

    private static string ExtractPreviewText(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            if (payload.ValueKind == JsonValueKind.String) return payload.GetString() ?? string.Empty;
            if (payload.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var key in new[] { "title", "toolName", "text", "message", "command" })
            {
                if (payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
        return string.Empty;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "\u2026";
}

public sealed record AgentActivityDto(
    AgentActivitySummaryDto Summary,
    IReadOnlyList<AgentActivityCardDto> Sessions,
    IReadOnlyList<AgentActivityWaitingCardDto> Waiting);

public sealed record AgentActivitySummaryDto(
    int Active,
    int Waiting,
    int Completed,
    int Failed,
    AgentActivitySlotUsageDto Slots);

public sealed record AgentActivitySlotUsageDto(int Active, int Max);

public sealed record AgentActivityCardDto(
    string IssueId,
    int IssueNumber,
    string IssueTitle,
    string IssueStage,
    string? IssueRuntimeStatus,
    string SessionId,
    [property: JsonPropertyName("status")] string Status,
    string? Model,
    string? Title,
    string CreatedAt,
    string? CompletedAt,
    string LastActivityAt,
    AgentActivityWorkItemDto? CurrentWorkItem,
    AgentActivityTaskProgressDto? TaskProgress,
    AgentActivityPreviewDto? LastActivity,
    string? FailureReason);

public sealed record AgentActivityWorkItemDto(string Type, string Id, string Title, string? Stage, string? SessionWorkType);
public sealed record AgentActivityTaskProgressDto(int Completed, int Total);
public sealed record AgentActivityPreviewDto(string Kind, string Text, string CreatedAt);
public sealed record AgentActivityWaitingCardDto(string IssueId, int IssueNumber, string IssueTitle, string? Stage, string Label, string? RequestedAt, string? Preview);
