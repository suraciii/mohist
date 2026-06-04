using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;

namespace Mohist.Server.Infrastructure.Persistence.Sessions;

public class WorkflowAgentSessionStore : IStateStore<WorkflowAgentSession>
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowAgentSessionStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowAgentSession?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowAgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<WorkflowAgentSession>> ListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.WorkflowAgentSessions.AsNoTracking().ToListAsync();
        return rows.Select(ToDomain).ToList();
    }

    public async Task SaveAsync(string key, WorkflowAgentSession state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = ToRow(state);
        row.Id = key;
        var existing = await db.WorkflowAgentSessions.FindAsync(key);
        if (existing is null)
        {
            db.WorkflowAgentSessions.Add(row);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(row);
        }
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.WorkflowAgentSessions.FindAsync(key);
        if (session is not null)
        {
            db.WorkflowAgentSessions.Remove(session);
            await db.SaveChangesAsync();
        }
    }

    private static WorkflowAgentSession ToDomain(WorkflowAgentSessionRow r) => new()
    {
        Id = r.Id,
        ProjectId = r.ProjectId,
        IssueNumber = r.IssueNumber,
        WorkflowRunId = r.WorkflowRunId,
        SessionName = r.SessionName,
        WorkId = r.WorkId,
        WorkType = r.WorkType,
        Stage = r.Stage,
        Title = r.Title,
        RunnerId = r.RunnerId,
        AgentSessionId = r.AgentSessionId,
        Status = AgentSessionStatusNames.Parse(r.Status),
        Model = r.Model,
        WorkDir = r.WorkDir,
        ChangeDir = r.ChangeDir,
        ProcessPid = r.ProcessPid,
        CreatedAt = r.CreatedAt,
        StartedAt = r.StartedAt,
        LastDataAt = r.LastDataAt,
        LastHeartbeatAt = r.LastHeartbeatAt,
        CompletedAt = r.CompletedAt,
        FailureReason = r.FailureReason,
        ExitCode = r.ExitCode,
        ResolvedModel = r.ResolvedModel,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        TotalTokens = r.TotalTokens,
        CachedReadTokens = r.CachedReadTokens,
        ThoughtTokens = r.ThoughtTokens,
        CostAmount = r.CostAmount,
        CostCurrency = r.CostCurrency,
        ContextWindowUsed = r.ContextWindowUsed,
        ContextWindowSize = r.ContextWindowSize,
        FailureCategory = r.FailureCategory,
        ToolCallCount = r.ToolCallCount,
        ToolErrorCount = r.ToolErrorCount,
    };

    private static WorkflowAgentSessionRow ToRow(WorkflowAgentSession s) => new()
    {
        Id = s.Id,
        ProjectId = s.ProjectId,
        IssueNumber = s.IssueNumber,
        WorkflowRunId = s.WorkflowRunId,
        SessionName = s.SessionName,
        WorkId = s.WorkId,
        WorkType = s.WorkType,
        Stage = s.Stage,
        Title = s.Title,
        RunnerId = s.RunnerId,
        AgentSessionId = s.AgentSessionId,
        Status = AgentSessionStatusNames.ToName(s.Status),
        Model = s.Model,
        WorkDir = s.WorkDir,
        ChangeDir = s.ChangeDir,
        ProcessPid = s.ProcessPid,
        CreatedAt = s.CreatedAt,
        StartedAt = s.StartedAt,
        LastDataAt = s.LastDataAt,
        LastHeartbeatAt = s.LastHeartbeatAt,
        CompletedAt = s.CompletedAt,
        FailureReason = s.FailureReason,
        ExitCode = s.ExitCode,
        ResolvedModel = s.ResolvedModel,
        InputTokens = s.InputTokens,
        OutputTokens = s.OutputTokens,
        TotalTokens = s.TotalTokens,
        CachedReadTokens = s.CachedReadTokens,
        ThoughtTokens = s.ThoughtTokens,
        CostAmount = s.CostAmount,
        CostCurrency = s.CostCurrency,
        ContextWindowUsed = s.ContextWindowUsed,
        ContextWindowSize = s.ContextWindowSize,
        FailureCategory = s.FailureCategory,
        ToolCallCount = s.ToolCallCount,
        ToolErrorCount = s.ToolErrorCount,
    };
}
