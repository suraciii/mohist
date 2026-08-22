using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Data.Sessions;

public enum AgentRetryOperationKind
{
    Root,
    Thread,
}

public enum AgentRetryOperationState
{
    Pending,
    Finished,
}

public sealed record AgentRetryOperation(
    string OperationId,
    string ProjectId,
    string IdempotencyKey,
    string SessionId,
    string TurnId,
    AgentRetryOperationKind Kind,
    string PreAllocatedSessionId,
    string PreAllocatedInputId,
    string PreAllocatedTurnId,
    AgentRetryOperationState State,
    string? ResultState,
    string? ResultText,
    string? ResultJobKey,
    string? ResultSessionId,
    string? ResultInputId,
    string? ResultTurnId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? FinishedAt)
{
    public bool IsPending => State == AgentRetryOperationState.Pending;
}

public sealed record AgentRetryOperationClaim(
    AgentRetryOperation Operation,
    bool AlreadyExists);

public sealed class AgentRetryOperationStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public AgentRetryOperationStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Claims one receipt using both unique boundaries. A loser of either
    /// race reads the committed winner and never gets a second operation.
    /// </summary>
    public async Task<AgentRetryOperationClaim> ClaimOrCreateAsync(
        string projectId,
        string sessionId,
        string turnId,
        string idempotencyKey,
        AgentRetryOperationKind kind,
        string preAllocatedSessionId,
        string preAllocatedInputId,
        string preAllocatedTurnId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await FindAsync(db, projectId, sessionId, turnId, idempotencyKey, ct);
        if (existing is not null)
            return new AgentRetryOperationClaim(ToDomain(existing), true);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var row = new AgentRetryOperationRow
        {
            OperationId = Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            IdempotencyKey = idempotencyKey,
            SessionId = sessionId,
            TurnId = turnId,
            Kind = kind.ToString().ToLowerInvariant(),
            PreAllocatedSessionId = preAllocatedSessionId,
            PreAllocatedInputId = preAllocatedInputId,
            PreAllocatedTurnId = preAllocatedTurnId,
            State = AgentRetryOperationState.Pending.ToString().ToLowerInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AgentRetryOperations.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return new AgentRetryOperationClaim(ToDomain(row), false);
        }
        catch (DbUpdateException ex)
        {
            // The unique indexes are the concurrency boundary. A fresh
            // context is required because the failed insert is tracked.
            await using var retryDb = await _dbFactory.CreateDbContextAsync(ct);
            var winner = await FindAsync(retryDb, projectId, sessionId, turnId, idempotencyKey, ct)
                ?? throw new InvalidOperationException("Retry operation insert lost a uniqueness race but no winner was readable.", ex);
            return new AgentRetryOperationClaim(ToDomain(winner), true);
        }
    }

    public async Task<AgentRetryOperation?> GetAsync(string projectId, string operationId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentRetryOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.OperationId == operationId, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<AgentRetryOperation?> FindExistingAsync(
        string projectId,
        string sessionId,
        string turnId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await FindAsync(db, projectId, sessionId, turnId, idempotencyKey, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<AgentRetryOperation>> ListPendingAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentRetryOperations.AsNoTracking()
            .Where(row => row.State == "pending")
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToArray();
    }

    public async Task MarkFinishedAsync(
        string operationId,
        string resultState,
        string resultText,
        string? resultJobKey = null,
        string? resultSessionId = null,
        string? resultInputId = null,
        string? resultTurnId = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AgentRetryOperations.SingleOrDefaultAsync(item => item.OperationId == operationId, ct)
            ?? throw new InvalidOperationException($"Retry operation '{operationId}' was not found.");
        if (row.State == "finished")
            return;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        row.State = "finished";
        row.ResultState = resultState;
        row.ResultText = resultText;
        row.ResultJobKey = resultJobKey;
        row.ResultSessionId = resultSessionId;
        row.ResultInputId = resultInputId;
        row.ResultTurnId = resultTurnId;
        row.UpdatedAt = now;
        row.FinishedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteFinishedBeforeAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentRetryOperations
            .Where(row => row.State == "finished" && row.FinishedAt != null && row.FinishedAt < cutoff)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return 0;
        db.AgentRetryOperations.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static async Task<AgentRetryOperationRow?> FindAsync(
        MohistDbContext db,
        string projectId,
        string sessionId,
        string turnId,
        string idempotencyKey,
        CancellationToken ct) =>
        await db.AgentRetryOperations.AsNoTracking().SingleOrDefaultAsync(row =>
            row.ProjectId == projectId
            && (row.IdempotencyKey == idempotencyKey
                || row.SessionId == sessionId && row.TurnId == turnId), ct);

    private static AgentRetryOperation ToDomain(AgentRetryOperationRow row) => new(
        row.OperationId,
        row.ProjectId,
        row.IdempotencyKey,
        row.SessionId,
        row.TurnId,
        Enum.TryParse<AgentRetryOperationKind>(row.Kind, ignoreCase: true, out var kind) ? kind : throw new InvalidOperationException($"Unknown retry operation kind '{row.Kind}'."),
        row.PreAllocatedSessionId,
        row.PreAllocatedInputId,
        row.PreAllocatedTurnId,
        Enum.TryParse<AgentRetryOperationState>(row.State, ignoreCase: true, out var state) ? state : throw new InvalidOperationException($"Unknown retry operation state '{row.State}'."),
        row.ResultState,
        row.ResultText,
        row.ResultJobKey,
        row.ResultSessionId,
        row.ResultInputId,
        row.ResultTurnId,
        row.CreatedAt,
        row.UpdatedAt,
        row.FinishedAt);
}
