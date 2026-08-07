using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Data.Events;

public sealed class DeadLetterStore : IDeadLetterStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public DeadLetterStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task WriteAsync(DeadLetterRow row, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.DeadLetters.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DeadLetterRow>> ListByHandlerAsync(
        string handler,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(handler))
            throw new ArgumentException("Handler must be provided.", nameof(handler));
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");

        // SQLite's EF provider cannot translate ORDER BY over DateTimeOffset,
        // so the ORDER BY runs in raw SQL and the materialized row is mapped
        // to DeadLetterRow by name. The WHERE clause stays parameterized.
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        const string sql = """
            SELECT "DeadLetterId", "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson",
                   "FailingHandler", "ErrorMessage", "ErrorStack", "AttemptCount", "DeadLetteredAt",
                   "Status", "RedeliveryAttemptedAt", "ResolvedAt"
            FROM "DeadLetters"
            WHERE "FailingHandler" = @handler
            ORDER BY "DeadLetteredAt" DESC, "DeadLetterId" DESC
            LIMIT @limit
            """;

        var handlerParameter = new SqliteParameter("@handler", handler);
        var limitParameter = new SqliteParameter("@limit", limit);
        var rows = await db.Database
            .SqlQueryRaw<DeadLetterSqlRow>(sql, handlerParameter, limitParameter)
            .ToListAsync(ct);

        return rows.Select(ToRow).ToList();
    }

    public async Task<IReadOnlyList<DeadLetterRow>> ListByTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        const string sql = """
            SELECT "DeadLetterId", "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson",
                   "FailingHandler", "ErrorMessage", "ErrorStack", "AttemptCount", "DeadLetteredAt",
                   "Status", "RedeliveryAttemptedAt", "ResolvedAt"
            FROM "DeadLetters"
            WHERE "DeadLetteredAt" >= @from AND "DeadLetteredAt" < @to
            ORDER BY "DeadLetteredAt" ASC, "DeadLetterId" ASC
            LIMIT @limit
            """;

        var fromParameter = new SqliteParameter("@from", from);
        var toParameter = new SqliteParameter("@to", to);
        var limitParameter = new SqliteParameter("@limit", limit);
        var rows = await db.Database
            .SqlQueryRaw<DeadLetterSqlRow>(sql, fromParameter, toParameter, limitParameter)
            .ToListAsync(ct);

        return rows.Select(ToRow).ToList();
    }

    public async Task RetryAsync(long deadLetterId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var deadLetter = await db.DeadLetters.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DeadLetterId == deadLetterId, ct);
        if (deadLetter is null)
            throw new InvalidOperationException($"Dead-letter row '{deadLetterId}' was not found.");

        var origin = ParseOrigin(deadLetter.Origin);
        switch (origin)
        {
            case EventOrigin.AgentSession:
            {
                var row = await db.AgentSessionEvents.FirstOrDefaultAsync(
                    e => e.Source == deadLetter.Source && e.Id == deadLetter.Id, ct);
                if (row is null)
                    throw new InvalidOperationException(
                        $"Agent session event '{deadLetter.Source}'/{deadLetter.Id} was not found.");
                row.DispatchedAt = null;
                break;
            }
            case EventOrigin.Issue:
            {
                var row = await db.IssueEvents.FirstOrDefaultAsync(
                    e => e.Source == deadLetter.Source && e.Id == deadLetter.Id, ct);
                if (row is null)
                    throw new InvalidOperationException(
                        $"Issue event '{deadLetter.Source}'/{deadLetter.Id} was not found.");
                row.DispatchedAt = null;
                break;
            }
            case EventOrigin.Epic:
            {
                var row = await db.EpicEvents.FirstOrDefaultAsync(
                    e => e.Source == deadLetter.Source && e.Id == deadLetter.Id, ct);
                if (row is null)
                    throw new InvalidOperationException(
                        $"Epic event '{deadLetter.Source}'/{deadLetter.Id} was not found.");
                row.DispatchedAt = null;
                break;
            }
            case EventOrigin.WorkflowRun:
            {
                var row = await db.WorkflowRunEvents.FirstOrDefaultAsync(
                    e => e.Source == deadLetter.Source && e.Id == deadLetter.Id, ct);
                if (row is null)
                    throw new InvalidOperationException(
                        $"Workflow run event '{deadLetter.Source}'/{deadLetter.Id} was not found.");
                row.DispatchedAt = null;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(deadLetter), deadLetter.Origin, "Unknown event origin.");
        }

        await db.SaveChangesAsync(ct);
    }

    private static EventOrigin ParseOrigin(string text) => text switch
    {
        nameof(EventOrigin.WorkflowRun) => EventOrigin.WorkflowRun,
        nameof(EventOrigin.Issue) => EventOrigin.Issue,
        nameof(EventOrigin.Epic) => EventOrigin.Epic,
        nameof(EventOrigin.AgentSession) => EventOrigin.AgentSession,
        nameof(EventOrigin.Workspace) => EventOrigin.Workspace,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    public async Task SettleAsync(
        UndeliveredEvent sourceEvent,
        IReadOnlyList<DeadLetterRow> rows,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceEvent);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new ArgumentException("At least one dead-letter row is required.", nameof(rows));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var handlers = rows.Select(row => row.FailingHandler).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.DeadLetters
            .Where(row => row.Source == sourceEvent.Source
                && row.Id == sourceEvent.Id
                && handlers.Contains(row.FailingHandler))
            .ToDictionaryAsync(row => row.FailingHandler, StringComparer.Ordinal, ct);

        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.FailingHandler, out var stored))
            {
                stored.ErrorMessage = row.ErrorMessage;
                stored.ErrorStack = row.ErrorStack;
                stored.AttemptCount = row.AttemptCount;
                stored.DeadLetteredAt = row.DeadLetteredAt;
                stored.Status = DeadLetterStatus.Pending;
                stored.RedeliveryAttemptedAt = null;
                stored.ResolvedAt = null;
            }
            else
            {
                db.DeadLetters.Add(row);
            }
        }

        await EventStore.SetDispatchedAsync(
            db,
            sourceEvent.Origin,
            sourceEvent.Source,
            sourceEvent.Id,
            dispatchedAt,
            ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default)
    {
        // SQLite's EF provider cannot translate ORDER BY over DateTimeOffset,
        // so the ORDER BY runs in raw SQL and the materialized row is mapped
        // to DeadLetterRow by name. The WHERE clause stays parameterized.
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        const string sql = """
            SELECT "DeadLetterId", "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson",
                   "FailingHandler", "ErrorMessage", "ErrorStack", "AttemptCount", "DeadLetteredAt",
                   "Status", "RedeliveryAttemptedAt", "ResolvedAt"
            FROM "DeadLetters"
            WHERE "Status" <> 'Resolved'
              AND (@filter IS NULL OR "FailingHandler" = @filter)
            ORDER BY "DeadLetteredAt", "DeadLetterId"
            LIMIT @limit
            """;

        var handlerParameter = new SqliteParameter("@filter", (object?)failingHandler ?? DBNull.Value);
        var limitParameter = new SqliteParameter("@limit", limit);
        var rows = await db.Database
            .SqlQueryRaw<DeadLetterSqlRow>(sql, handlerParameter, limitParameter)
            .ToListAsync(ct);

        return rows.Select(ToRow).ToList();
    }

    public async Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.DeadLetters.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DeadLetterId == deadLetterId, ct);
        return row is null ? null : ToRow(row);
    }

    public async Task<DeadLetterRow?> StartRedeliveryAsync(
        long deadLetterId,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.DeadLetters.FirstOrDefaultAsync(r => r.DeadLetterId == deadLetterId, ct);
        if (row is null || row.Status == DeadLetterStatus.Resolved)
            return null;

        row.Status = DeadLetterStatus.Redelivering;
        row.RedeliveryAttemptedAt = attemptedAt;
        await db.SaveChangesAsync(ct);
        return ToRow(row);
    }

    public async Task RecordRedeliveryFailureAsync(
        long deadLetterId,
        string errorMessage,
        string? errorStack,
        int attemptCount,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.DeadLetters.FirstOrDefaultAsync(r => r.DeadLetterId == deadLetterId, ct)
            ?? throw new InvalidOperationException($"Dead-letter row '{deadLetterId}' was not found.");
        row.Status = DeadLetterStatus.Pending;
        row.ErrorMessage = errorMessage;
        row.ErrorStack = errorStack;
        row.AttemptCount = attemptCount;
        row.RedeliveryAttemptedAt = attemptedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResolveAsync(
        long deadLetterId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.DeadLetters.FirstOrDefaultAsync(r => r.DeadLetterId == deadLetterId, ct)
            ?? throw new InvalidOperationException($"Dead-letter row '{deadLetterId}' was not found.");
        row.Status = DeadLetterStatus.Resolved;
        row.ResolvedAt = resolvedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long deadLetterId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.DeadLetters
            .Where(row => row.DeadLetterId == deadLetterId)
            .ExecuteDeleteAsync(ct);
    }

    private static DeadLetterRow ToRow(DeadLetterSqlRow row) =>
        new()
        {
            DeadLetterId = row.DeadLetterId,
            Origin = row.Origin,
            Id = row.Id,
            Source = row.Source,
            EventId = row.EventId,
            Type = row.Type,
            Time = row.Time,
            SpecVersion = row.SpecVersion,
            Subject = row.Subject,
            DataContentType = row.DataContentType,
            Data = ParseJsonElement(row.Data),
            ExtensionsJson = row.ExtensionsJson,
            FailingHandler = row.FailingHandler,
            ErrorMessage = row.ErrorMessage,
            ErrorStack = row.ErrorStack,
            AttemptCount = row.AttemptCount,
            DeadLetteredAt = row.DeadLetteredAt,
            Status = Enum.Parse<DeadLetterStatus>(row.Status, ignoreCase: false),
            RedeliveryAttemptedAt = row.RedeliveryAttemptedAt,
            ResolvedAt = row.ResolvedAt,
        };

    private static DeadLetterRow ToRow(DeadLetterRow row) =>
        new()
        {
            DeadLetterId = row.DeadLetterId,
            Origin = row.Origin,
            Id = row.Id,
            Source = row.Source,
            EventId = row.EventId,
            Type = row.Type,
            Time = row.Time,
            SpecVersion = row.SpecVersion,
            Subject = row.Subject,
            DataContentType = row.DataContentType,
            Data = row.Data,
            ExtensionsJson = row.ExtensionsJson,
            FailingHandler = row.FailingHandler,
            ErrorMessage = row.ErrorMessage,
            ErrorStack = row.ErrorStack,
            AttemptCount = row.AttemptCount,
            DeadLetteredAt = row.DeadLetteredAt,
            Status = row.Status,
            RedeliveryAttemptedAt = row.RedeliveryAttemptedAt,
            ResolvedAt = row.ResolvedAt,
        };

    private static JsonElement ParseJsonElement(string json) =>
        JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json).RootElement.Clone();

    private sealed class DeadLetterSqlRow
    {
        public long DeadLetterId { get; set; }
        public string Origin { get; set; } = "";
        public long Id { get; set; }
        public string Source { get; set; } = "";
        public string EventId { get; set; } = "";
        public string Type { get; set; } = "";
        public DateTimeOffset Time { get; set; }
        public string SpecVersion { get; set; } = "";
        public string? Subject { get; set; }
        public string DataContentType { get; set; } = "";
        public string Data { get; set; } = "null";
        public string ExtensionsJson { get; set; } = "{}";
        public string FailingHandler { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public string? ErrorStack { get; set; }
        public int AttemptCount { get; set; }
        public DateTimeOffset DeadLetteredAt { get; set; }
        public string Status { get; set; } = nameof(DeadLetterStatus.Pending);
        public DateTimeOffset? RedeliveryAttemptedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
    }
}
