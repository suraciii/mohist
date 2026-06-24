using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services.Sessions;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Workflow.Services;

public class WorkflowActivityQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowQuerier _workflowQuerier;
    private readonly AgentSessionQuery _sessionQuery;

    public WorkflowActivityQuerier(IDbContextFactory<MohistDbContext> dbFactory, WorkflowQuerier workflowQuerier, AgentSessionQuery sessionQuery)
    {
        _dbFactory = dbFactory;
        _workflowQuerier = workflowQuerier;
        _sessionQuery = sessionQuery;
    }

    public async Task<IReadOnlyList<ActiveAgentDto>> ListActiveAgentsAsync(string? projectId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = string.IsNullOrWhiteSpace(projectId)
            ? await ListAllSessionsAsync(db, ct)
            : await _sessionQuery.ListByLabelsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                },
                AgentSessionQueryOrder.CreatedDescending,
                ct: ct);
        var result = new List<ActiveAgentDto>();

        foreach (var record in sessions.Where(IsActive))
        {
            var session = record.Session;
            var workflowRunId = Label(record, AgentSessionQueryMetadataKeys.WorkflowRunId);
            var workId = Label(record, AgentSessionQueryMetadataKeys.WorkId);
            if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(workId))
                continue;

            var status = await _workflowQuerier.GetStatusAsync(workflowRunId);
            var pending = status?.PendingWork;
            if (status is null || pending is null || pending.WorkId != workId) continue;

            var currentStage = status.Stages.FirstOrDefault(s => s.Stage == pending.Stage);
            var completed = currentStage?.Tasks.Count(t => string.Equals(t.Status, TaskRunStatus.Completed.ToString(), StringComparison.Ordinal)) ?? 0;
            var total = currentStage?.Tasks.Count ?? 0;
            var lastActivity = LastActivityAt(session).ToString("o");
            var recordProjectId = Label(record, AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty;
            var issueNumber = IssueNumber(record);
            var workType = Label(record, AgentSessionQueryMetadataKeys.WorkType) ?? string.Empty;
            var stage = Label(record, AgentSessionQueryMetadataKeys.Stage);

            result.Add(new ActiveAgentDto(
                session.Runtime.RunnerId,
                $"issue_{recordProjectId}_{issueNumber}",
                issueNumber,
                recordProjectId,
                workflowRunId,
                workId,
                workType,
                stage,
                null,
                session.Id,
                (session.Status.BoundAt ?? session.Status.CreatedAt).ToString("o"),
                lastActivity,
                new ActiveAgentProgressDto(
                    stage,
                    new ActiveWorkItemDto(workType == string.Empty ? "task" : workType, workId, workId),
                    new TaskProgressDto(completed, total),
                    lastActivity)));
        }

        return result;
    }

    private static DateTime LastActivityAt(AgentSession session) =>
        session.Status.LastDataAt ?? session.Status.BoundAt ?? session.Status.CreatedAt;

    private static bool IsActive(AgentSessionRecord record) => record.Row.AgentSessionId is not null;

    private static string? Label(AgentSessionRecord record, string key) =>
        record.Label(key) ?? record.Session.Metadata.Label(key);

    private static int IssueNumber(AgentSessionRecord record) =>
        int.TryParse(Label(record, AgentSessionQueryMetadataKeys.IssueNumber), out var issueNumber)
            ? issueNumber
            : 0;

    private static async Task<IReadOnlyList<AgentSessionRecord>> ListAllSessionsAsync(MohistDbContext db, CancellationToken ct)
    {
        var rows = await db.AgentSessions.AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var result = new List<AgentSessionRecord>(rows.Count);
        foreach (var row in rows)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is null) continue;
            result.Add(new AgentSessionRecord(
                row,
                session,
                session.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        return result;
    }
}

public sealed record ActiveAgentDto(string RunnerId, string IssueId, int IssueNumber, string ProjectId, string WorkflowRunId, string WorkId, string WorkType, string? Stage, string? Title, string SessionId, string StartedAt, string LastActivityAt, ActiveAgentProgressDto Progress);
public sealed record ActiveAgentProgressDto(string? Stage, ActiveWorkItemDto CurrentWorkItem, TaskProgressDto TaskProgress, string LastActivityAt);
public sealed record ActiveWorkItemDto(string Type, string Id, string Title);
public sealed record TaskProgressDto(int Completed, int Total);
