using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Sessions.GrainStorage;

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

    private static WorkflowAgentSession ToDomain(WorkflowAgentSessionRow r) => WorkflowAgentSession.Restore(
        r.Id, r.ProjectId, r.IssueNumber, r.WorkflowRunId, r.SessionName,
        r.WorkId, r.WorkType, r.Stage, r.Title, r.RunnerId, r.AgentSessionId,
        AgentSessionStatusNames.Parse(r.Status), r.Model,
        r.WorkDir, r.ChangeDir, r.ProcessPid,
        r.CreatedAt, r.StartedAt, r.LastDataAt, r.LastHeartbeatAt,
        r.CompletedAt, r.FailureReason, r.ExitCode);

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
    };
}
