using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Data.Auth;

public sealed class AuthAuditEventStore : IAuthAuditEventStore, IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AuthAuditEventStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task RecordAsync(AuthAuditEvent auditEvent, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AuthAuditEvents.Add(new AuthAuditEventRow
        {
            Id = auditEvent.Id,
            SubjectId = auditEvent.SubjectId,
            EventType = auditEvent.EventType.ToString(),
            TargetKind = auditEvent.TargetKind,
            TargetId = auditEvent.TargetId,
            OccurredAt = auditEvent.OccurredAt,
            MetadataJson = JSON.Serialize(auditEvent.Metadata),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuthAuditEvent>> ListAsync(
        AuthAuditEventType? eventType = null,
        DateTimeOffset? since = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // SQLite cannot translate DateTimeOffset ordering/comparisons in
        // LINQ, so the projection is raw SQL with typed parameters — same
        // pattern as DeadLetterStore's time-range read.
        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();
        if (eventType is not null)
        {
            conditions.Add("\"EventType\" = @eventType");
            parameters.Add(new SqliteParameter("@eventType", eventType.Value.ToString()));
        }
        if (since is not null)
        {
            conditions.Add("\"OccurredAt\" >= @since");
            parameters.Add(new SqliteParameter("@since", since.Value));
        }

        var sql = """
            SELECT "Id", "SubjectId", "EventType", "TargetKind", "TargetId", "OccurredAt", "MetadataJson"
            FROM "AuthAuditEvents"
            """;
        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);
        sql += """

            ORDER BY "OccurredAt" DESC, "Id" DESC
            LIMIT @limit
            """;
        parameters.Add(new SqliteParameter("@limit", limit));

        var rows = await db.Database
            .SqlQueryRaw<AuthAuditEventRow>(sql, parameters.ToArray())
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var events = new List<AuthAuditEvent>(rows.Count);
        foreach (var row in rows)
        {
            // Unknown stored types (a future server wrote them) are
            // skipped, same discipline as unknown credential kinds.
            if (!Enum.TryParse<AuthAuditEventType>(row.EventType, ignoreCase: true, out var parsedType))
                continue;
            events.Add(new AuthAuditEvent(
                row.Id,
                row.SubjectId,
                parsedType,
                row.TargetKind,
                row.TargetId,
                row.OccurredAt,
                JSON.DeserializeDictionary(row.MetadataJson)));
        }

        return events;
    }
}
