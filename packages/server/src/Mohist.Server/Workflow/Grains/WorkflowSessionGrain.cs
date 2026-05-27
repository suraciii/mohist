using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Sessions.Storage;

namespace Mohist.Server.Workflow.Grains;

public class WorkflowSessionGrain : Grain, IWorkflowSessionGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private WorkflowSessionRecord? _session;
    private long _nextSequence;

    public WorkflowSessionGrain(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private string Id => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _session = await db.WorkflowSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Id, ct);
        _nextSequence = _session is null
            ? 0
            : await db.WorkflowSessionEvents
                .Where(e => e.WorkflowSessionId == Id)
                .Select(e => (long?)e.Sequence)
                .MaxAsync(ct) ?? 0;
    }

    public async Task<WorkflowSessionSnapshot> EnsureAsync(EnsureWorkflowSessionCommand command)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.WorkflowSessions.FirstOrDefaultAsync(s => s.Id == Id)
            ?? await db.WorkflowSessions.FirstOrDefaultAsync(s => s.WorkflowRunId == command.WorkflowRunId && s.SessionName == command.SessionName);

        if (session is null)
        {
            session = new WorkflowSessionRecord
            {
                Id = Id,
                WorkflowRunId = command.WorkflowRunId,
                SessionName = command.SessionName,
                ProjectId = command.ProjectId,
                IssueNumber = command.IssueNumber,
                RunnerId = command.RunnerId,
                Status = "created",
                CreatedAt = DateTime.UtcNow,
            };
            db.WorkflowSessions.Add(session);
            await db.SaveChangesAsync();
        }
        else
        {
            session.RunnerId = command.RunnerId;
            session.ProjectId ??= command.ProjectId;
            session.IssueNumber ??= command.IssueNumber;
            await db.SaveChangesAsync();
        }

        _session = Clone(session);
        _nextSequence = await db.WorkflowSessionEvents
            .Where(e => e.WorkflowSessionId == _session.Id)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? 0;
        return ToSnapshot(_session);
    }

    public async Task<WorkflowSessionSnapshot> AttachAcpSessionAsync(AttachAcpSessionCommand command)
    {
        var session = await LoadTrackedOrCreateAsync();
        var now = DateTime.UtcNow;
        session.AcpSessionId = command.AcpSessionId;
        session.WorkDir = command.WorkDir ?? session.WorkDir;
        session.Model = command.Model ?? session.Model;
        session.ProcessPid = command.ProcessPid ?? session.ProcessPid;
        session.StartedAt ??= now;
        session.LastDataAt = now;
        session.Status = "running";
        await SaveAsync(session);
        return ToSnapshot(session);
    }

    public async Task<IReadOnlyList<WorkflowSessionEventSnapshot>> AppendEventsAsync(AppendWorkflowSessionEventsCommand command)
    {
        if (command.Events.Count == 0) return [];

        var session = await LoadTrackedOrCreateAsync();
        var now = DateTime.UtcNow;
        var records = command.Events.Select(e => new WorkflowSessionEventRecord
        {
            WorkflowSessionId = session.Id,
            WorkflowRunId = session.WorkflowRunId,
            SessionName = session.SessionName,
            AcpSessionId = session.AcpSessionId,
            ProjectId = session.ProjectId,
            IssueNumber = session.IssueNumber,
            WorkId = command.WorkId,
            WorkType = command.WorkType,
            Stage = command.Stage,
            Sequence = ++_nextSequence,
            Type = e.Type,
            PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
            CreatedAt = now,
        }).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkflowSessionEvents.AddRange(records);
        var tracked = await db.WorkflowSessions.FirstAsync(s => s.Id == session.Id);
        tracked.LastDataAt = now;
        if (tracked.Status is "created") tracked.Status = "running";
        await db.SaveChangesAsync();
        _session = Clone(tracked);
        return records.Select(ToSnapshot).ToList();
    }

    public async Task<WorkflowSessionSnapshot> MarkStatusAsync(WorkflowSessionStatusCommand command)
    {
        var session = await LoadTrackedOrCreateAsync();
        if (IsTerminal(session.Status)) return ToSnapshot(session);
        session.Status = command.Status;
        session.LastDataAt = command.LastDataAt ?? DateTime.UtcNow;
        session.FailureReason = command.FailureReason ?? session.FailureReason;
        await SaveAsync(session);
        return ToSnapshot(session);
    }

    public async Task<WorkflowSessionSnapshot> CompleteAsync(CompleteWorkflowSessionCommand command)
    {
        var session = await LoadTrackedOrCreateAsync();
        if (IsTerminal(session.Status)) return ToSnapshot(session);
        var now = DateTime.UtcNow;
        session.Status = ToTerminalStatus(command.Status);
        session.CompletedAt = now;
        session.LastDataAt = now;
        session.FailureReason = command.FailureReason;
        session.ExitCode = command.ExitCode;
        await SaveAsync(session);
        return ToSnapshot(session);
    }

    public Task<WorkflowSessionSnapshot?> GetAsync() => Task.FromResult(_session is null ? null : ToSnapshot(_session));

    private async Task<WorkflowSessionRecord> LoadTrackedOrCreateAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.WorkflowSessions.FirstOrDefaultAsync(s => s.Id == Id);
        if (session is not null) return session;
        session = new WorkflowSessionRecord
        {
            Id = Id,
            WorkflowRunId = Id.Split(':', 2)[0],
            SessionName = Id.Contains(':', StringComparison.Ordinal) ? Id.Split(':', 2)[1] : Id,
            Status = "created",
            CreatedAt = DateTime.UtcNow,
        };
        db.WorkflowSessions.Add(session);
        await db.SaveChangesAsync();
        _session = Clone(session);
        return session;
    }

    private async Task SaveAsync(WorkflowSessionRecord session)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkflowSessions.Update(session);
        await db.SaveChangesAsync();
        _session = Clone(session);
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";

    private static string ToTerminalStatus(string status) => status switch
    {
        "success" or "completed" or "pass" or "passed" => "completed",
        "cancelled" => "cancelled",
        _ => "failed",
    };

    private static WorkflowSessionRecord Clone(WorkflowSessionRecord s) => new()
    {
        Id = s.Id,
        WorkflowRunId = s.WorkflowRunId,
        SessionName = s.SessionName,
        AcpSessionId = s.AcpSessionId,
        ProjectId = s.ProjectId,
        IssueNumber = s.IssueNumber,
        RunnerId = s.RunnerId,
        Status = s.Status,
        Model = s.Model,
        WorkDir = s.WorkDir,
        ProcessPid = s.ProcessPid,
        CreatedAt = s.CreatedAt,
        StartedAt = s.StartedAt,
        LastDataAt = s.LastDataAt,
        CompletedAt = s.CompletedAt,
        FailureReason = s.FailureReason,
        ExitCode = s.ExitCode,
    };

    private static WorkflowSessionSnapshot ToSnapshot(WorkflowSessionRecord s) => new(
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

    private static WorkflowSessionEventSnapshot ToSnapshot(WorkflowSessionEventRecord e) => new(
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
        e.PayloadJson,
        e.CreatedAt.ToString("o"));
}
