using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Runner;

public class RunnerWorkStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public RunnerWorkStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task InsertOutstandingAsync(RunnerWork work, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.RunnerWorks.Add(Map(work));
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryMarkTerminalAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId,
        RunnerWorkStatus terminalStatus,
        string? reason,
        DateTimeOffset finishedAt,
        CancellationToken ct = default)
    {
        if (terminalStatus == RunnerWorkStatus.Outstanding)
            throw new ArgumentException("terminalStatus must be Completed or Failed", nameof(terminalStatus));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await FindLatestOutstandingAsync(db, runnerId, ownerKind, ownerId, workId, ct);
        if (row is null)
            return false;

        row.Status = StatusString(terminalStatus);
        row.Reason = reason;
        row.FinishedAt = finishedAt;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<RunnerWork>> ListOutstandingAsync(string runnerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.RunnerWorks
            .AsNoTracking()
            .Where(r => r.RunnerId == runnerId && r.Status == "outstanding")
            .OrderByDescending(r => r.Id)
            .Select(r => Map(r))
            .ToListAsync(ct);
    }

    public async Task<RunnerWork?> FindAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RunnerWorks
            .Where(r =>
                r.RunnerId == runnerId &&
                r.OwnerKind == ownerKind &&
                r.OwnerId == ownerId &&
                r.WorkId == workId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : Map(row);
    }

    private static async Task<RunnerWorkRow?> FindLatestOutstandingAsync(
        MohistDbContext db,
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId,
        CancellationToken ct)
    {
        return await db.RunnerWorks
            .Where(r =>
                r.RunnerId == runnerId &&
                r.OwnerKind == ownerKind &&
                r.OwnerId == ownerId &&
                r.WorkId == workId &&
                r.Status == "outstanding")
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static RunnerWorkRow Map(RunnerWork work)
    {
        return new RunnerWorkRow
        {
            RunnerId = work.RunnerId,
            OwnerKind = work.OwnerKind,
            OwnerId = work.OwnerId,
            WorkId = work.WorkId,
            TakenAt = work.TakenAt,
            Status = StatusString(work.Status),
            Reason = work.Reason,
            FinishedAt = work.FinishedAt,
        };
    }

    private static RunnerWork Map(RunnerWorkRow row)
    {
        return new RunnerWork(
            row.RunnerId,
            row.OwnerKind,
            row.OwnerId,
            row.WorkId,
            row.TakenAt,
            ParseStatus(row.Status),
            row.Reason,
            row.FinishedAt);
    }

    private static string StatusString(RunnerWorkStatus status) => status switch
    {
        RunnerWorkStatus.Outstanding => "outstanding",
        RunnerWorkStatus.Completed => "completed",
        RunnerWorkStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status"),
    };

    private static RunnerWorkStatus ParseStatus(string status) => status switch
    {
        "outstanding" => RunnerWorkStatus.Outstanding,
        "completed" => RunnerWorkStatus.Completed,
        "failed" => RunnerWorkStatus.Failed,
        _ => RunnerWorkStatus.Outstanding,
    };
}
