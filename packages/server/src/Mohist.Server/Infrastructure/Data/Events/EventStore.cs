using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Data.Events;

public class EventStore : IEventStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<EventStore> _log;

    public EventStore(IDbContextFactory<MohistDbContext> dbFactory, ILogger<EventStore> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    public async Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        var source = envelope.Source.ToString();
        _log.LogInformation(
            "[event-store] AppendAsync: incoming source={Source} type={Type} eventId={EventId} subject={Subject}",
            source, envelope.Type, envelope.Id, envelope.Subject);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (source.StartsWith(IssueEventPersistence.SourcePrefix, StringComparison.Ordinal))
        {
            var nextId = await NextIssueIdAsync(source, ct);
            db.IssueEvents.Add(new IssueEventRow
            {
                Id = nextId,
                Source = source,
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? JsonDocument.Parse("null").RootElement,
                ExtensionsJson = SerializeExtensions(envelope.Extensions),
            });
            try
            {
                var saved = await db.SaveChangesAsync(ct);
                _log.LogInformation(
                    "[event-store] AppendAsync: persisted source={Source} type={Type} id={Id} eventId={EventId} rows={Rows}",
                    source, envelope.Type, nextId, envelope.Id, saved);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[event-store] AppendAsync: FAILED source={Source} type={Type} eventId={EventId} at id={Id}",
                    source, envelope.Type, envelope.Id, nextId);
                throw;
            }
            return;
        }

        if (source.StartsWith(EpicEventPersistence.SourcePrefix, StringComparison.Ordinal))
        {
            var nextId = await NextEpicIdAsync(source, ct);
            db.EpicEvents.Add(new EpicEventRow
            {
                Id = nextId,
                Source = source,
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? JsonDocument.Parse("null").RootElement,
                ExtensionsJson = SerializeExtensions(envelope.Extensions),
            });
            try
            {
                var saved = await db.SaveChangesAsync(ct);
                _log.LogInformation(
                    "[event-store] AppendAsync: persisted source={Source} type={Type} id={Id} eventId={EventId} rows={Rows}",
                    source, envelope.Type, nextId, envelope.Id, saved);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[event-store] AppendAsync: FAILED source={Source} type={Type} eventId={EventId} at id={Id}",
                    source, envelope.Type, envelope.Id, nextId);
                throw;
            }
            return;
        }

        var workflowNextId = await NextWorkflowIdAsync(source, ct);
        db.WorkflowRunEvents.Add(new WorkflowRunEventRow
        {
            Id = workflowNextId,
            Source = source,
            EventId = envelope.Id,
            Type = envelope.Type,
            Time = envelope.Time,
            SpecVersion = envelope.SpecVersion,
            Subject = envelope.Subject,
            DataContentType = envelope.DataContentType ?? "application/json",
            Data = envelope.Data ?? JsonDocument.Parse("null").RootElement,
            ExtensionsJson = SerializeExtensions(envelope.Extensions),
        });
        try
        {
            var saved = await db.SaveChangesAsync(ct);
            _log.LogInformation(
                "[event-store] AppendAsync: persisted source={Source} type={Type} id={Id} eventId={EventId} rows={Rows}",
                source, envelope.Type, workflowNextId, envelope.Id, saved);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[event-store] AppendAsync: FAILED source={Source} type={Type} eventId={EventId} at id={Id}",
                source, envelope.Type, envelope.Id, workflowNextId);
            throw;
        }
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
    {
        var source = WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowRunEvents.AsNoTracking()
            .Where(e => e.Source == source)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        return rows.Select(ToStored).ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default)
    {
        var source = IssueEventPersistence.IssueSource(issueId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.IssueEvents.AsNoTracking()
            .Where(e => e.Source == source)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        return rows.Select(ToIssueStored).ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string epicId, int limit = 200, CancellationToken ct = default)
    {
        var source = EpicEventPersistence.EpicSource(epicId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.EpicEvents.AsNoTracking()
            .Where(e => e.Source == source)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        return rows.Select(ToEpicStored).ToList();
    }

    public async Task MarkDispatchedAsync(string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (source.StartsWith(IssueEventPersistence.SourcePrefix, StringComparison.Ordinal))
        {
            var row = await db.IssueEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
            if (row is null)
                throw new InvalidOperationException($"Issue event '{source}'/{id} was not found.");
            row.DispatchedAt = dispatchedAt;
        }
        else if (source.StartsWith(EpicEventPersistence.SourcePrefix, StringComparison.Ordinal))
        {
            var row = await db.EpicEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
            if (row is null)
                throw new InvalidOperationException($"Epic event '{source}'/{id} was not found.");
            row.DispatchedAt = dispatchedAt;
        }
        else if (source.StartsWith(WorkflowRunEventPersistence.SourcePrefix, StringComparison.Ordinal))
        {
            var row = await db.WorkflowRunEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
            if (row is null)
                throw new InvalidOperationException($"Workflow run event '{source}'/{id} was not found.");
            row.DispatchedAt = dispatchedAt;
        }
        else
        {
            throw new ArgumentException($"Unrecognized event source '{source}'.", nameof(source));
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        const string sql = """
            SELECT 'WorkflowRun' AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "WorkflowRunEvents" WHERE "DispatchedAt" IS NULL
            UNION ALL
            SELECT 'Issue' AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "IssueEvents" WHERE "DispatchedAt" IS NULL
            UNION ALL
            SELECT 'Epic' AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "EpicEvents" WHERE "DispatchedAt" IS NULL
            ORDER BY "Source", "Id"
            LIMIT @limit
            """;

        var parameter = new Microsoft.Data.Sqlite.SqliteParameter("@limit", limit);
        var rows = await db.Database
            .SqlQueryRaw<UndeliveredSqlRow>(sql, parameter)
            .ToListAsync(ct);

        return rows.Select(ToUndeliveredEvent).ToList();
    }

    private static StoredCloudEvent ToStored(WorkflowRunEventRow row) =>
        new(row.Id, new CloudEvent(
            id: row.EventId,
            source: new Uri(row.Source, UriKind.RelativeOrAbsolute),
            type: row.Type,
            time: row.Time,
            data: row.Data,
            dataContentType: row.DataContentType,
            subject: row.Subject,
            specVersion: row.SpecVersion,
            extensions: DeserializeExtensions(row.ExtensionsJson)));

    private static StoredCloudEvent ToIssueStored(IssueEventRow row) =>
        new(row.Id, new CloudEvent(
            id: row.EventId,
            source: new Uri(row.Source, UriKind.RelativeOrAbsolute),
            type: row.Type,
            time: row.Time,
            data: row.Data,
            dataContentType: row.DataContentType,
            subject: row.Subject,
            specVersion: row.SpecVersion,
            extensions: DeserializeExtensions(row.ExtensionsJson)));

    private static StoredCloudEvent ToEpicStored(EpicEventRow row) =>
        new(row.Id, new CloudEvent(
            id: row.EventId,
            source: new Uri(row.Source, UriKind.RelativeOrAbsolute),
            type: row.Type,
            time: row.Time,
            data: row.Data,
            dataContentType: row.DataContentType,
            subject: row.Subject,
            specVersion: row.SpecVersion,
            extensions: DeserializeExtensions(row.ExtensionsJson)));

    private static string SerializeExtensions(IReadOnlyDictionary<string, string> extensions) =>
        JsonSerializer.Serialize(extensions, CloudEvent.JsonOptions);

    private static IReadOnlyDictionary<string, string> DeserializeExtensions(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, CloudEvent.JsonOptions)
            ?? new Dictionary<string, string>();

    private async Task<long> NextWorkflowIdAsync(string source, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await db.WorkflowRunEvents
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync(ct) ?? 0) + 1;
    }

    private async Task<long> NextIssueIdAsync(string source, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await db.IssueEvents
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync(ct) ?? 0) + 1;
    }

    private async Task<long> NextEpicIdAsync(string source, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await db.EpicEvents
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync(ct) ?? 0) + 1;
    }

    private sealed class UndeliveredSqlRow
    {
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
    }

    private static UndeliveredEvent ToUndeliveredEvent(UndeliveredSqlRow row) =>
        new(
            Origin: ParseOrigin(row.Origin),
            Id: row.Id,
            Source: row.Source,
            EventId: row.EventId,
            Type: row.Type,
            Time: row.Time,
            SpecVersion: row.SpecVersion,
            Subject: row.Subject,
            DataContentType: row.DataContentType,
            Data: ParseJsonElement(row.Data),
            ExtensionsJson: row.ExtensionsJson);

    private static EventOrigin ParseOrigin(string text) => text switch
    {
        "WorkflowRun" => EventOrigin.WorkflowRun,
        "Issue" => EventOrigin.Issue,
        "Epic" => EventOrigin.Epic,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    private static JsonElement ParseJsonElement(string json) =>
        JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json).RootElement.Clone();
}
