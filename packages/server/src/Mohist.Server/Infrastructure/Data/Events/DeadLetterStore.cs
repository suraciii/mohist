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

    public async Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default)
    {
        // SQLite's EF provider cannot translate ORDER BY over DateTimeOffset,
        // so the ORDER BY runs in raw SQL and the materialized row is mapped
        // to DeadLetterRow by name. The WHERE clause stays parameterized.
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        const string sql = """
            SELECT "DeadLetterId", "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson",
                   "FailingHandler", "ErrorMessage", "ErrorStack", "AttemptCount", "DeadLetteredAt"
            FROM "DeadLetters"
            WHERE @filter IS NULL OR "FailingHandler" = @filter
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
    }
}
