using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Sessions.Storage;

namespace Mohist.Server.Workflow.Sessions.Queries;

public class WorkflowSessionQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowSessionQueryService(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByWorkflowAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowSessions.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<WorkflowSessionDetailDto?> GetByWorkflowAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.WorkflowSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.SessionName == sessionName, ct);
        if (session is null) return null;

        var events = await db.WorkflowSessionEvents.AsNoTracking()
            .Where(e => e.WorkflowSessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        return new WorkflowSessionDetailDto(ToDto(session), events.Select(ToDto).ToList());
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.IssueNumber == issueNumber)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private static WorkflowSessionDto ToDto(WorkflowSessionRecord s) => new(
        s.Id,
        s.WorkflowRunId,
        s.SessionName,
        s.AcpSessionId,
        s.ProjectId,
        s.IssueNumber,
        s.RunnerId,
        s.Status,
        s.Model,
        s.WorkDir,
        s.ProcessPid,
        s.CreatedAt.ToString("o"),
        s.StartedAt?.ToString("o"),
        s.LastDataAt?.ToString("o"),
        s.CompletedAt?.ToString("o"),
        s.FailureReason,
        s.ExitCode);

    private static WorkflowSessionEventDto ToDto(WorkflowSessionEventRecord e) => new(
        e.Id.ToString(),
        e.WorkflowSessionId,
        e.WorkflowRunId,
        e.SessionName,
        e.AcpSessionId,
        e.ProjectId,
        e.IssueNumber,
        e.WorkId,
        e.WorkType,
        e.Stage,
        e.Sequence,
        e.Type,
        ParsePayload(e.PayloadJson),
        e.CreatedAt.ToString("o"));

    private static object? ParsePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return json;
        }
    }
}
