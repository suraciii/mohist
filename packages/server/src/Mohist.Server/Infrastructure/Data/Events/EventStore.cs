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

        var nextId = await NextIdAsync(source, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.WorkflowRunEvents.Add(new WorkflowRunEventRow
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

    private static string SerializeExtensions(IReadOnlyDictionary<string, string> extensions) =>
        JsonSerializer.Serialize(extensions, CloudEvent.JsonOptions);

    private static IReadOnlyDictionary<string, string> DeserializeExtensions(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, CloudEvent.JsonOptions)
            ?? new Dictionary<string, string>();

    private async Task<long> NextIdAsync(string source, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await db.WorkflowRunEvents
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync(ct) ?? 0) + 1;
    }
}
