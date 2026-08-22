using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Data.Events;

public class EventStore : IEventStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<EventStore> _log;
    private readonly IPublicProjectionNudge? _publicProjectionNudge;

    public EventStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<EventStore> log,
        IPublicProjectionNudge? publicProjectionNudge = null)
    {
        _dbFactory = dbFactory;
        _log = log;
        _publicProjectionNudge = publicProjectionNudge;
    }

    /// <summary>
    /// Best-effort latency nudge for the public execution projector
    /// after a durable journal append; the projector's timer sweep is
    /// the correctness safety net.
    /// </summary>
    private void NudgePublicProjectionBestEffort()
    {
        try
        {
            _publicProjectionNudge?.Nudge();
        }
        catch
        {
            // A nudge is advisory only.
        }
    }

    public async Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        var source = envelope.Source.ToString();
        _log.LogInformation(
            "[event-store] AppendAsync: incoming source={Source} type={Type} eventId={EventId} subject={Subject}",
            source, envelope.Type, envelope.Id, envelope.Subject);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await AppendAsync(db, envelope, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            NudgePublicProjectionBestEffort();
            _log.LogInformation(
                "[event-store] AppendAsync: persisted source={Source} type={Type} eventId={EventId}",
                source, envelope.Type, envelope.Id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[event-store] AppendAsync: FAILED source={Source} type={Type} eventId={EventId}",
                source, envelope.Type, envelope.Id);
            throw;
        }
    }

    public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
    {
        var source = envelope.Source.ToString();

        if (source.StartsWith(AgentSessionEventPersistence.SourcePrefix, StringComparison.Ordinal))
        {
            var nextId = await NextAgentSessionIdAsync(db, source, ct);
            db.AgentSessionEvents.Add(new AgentSessionEventRow
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
            return;
        }

        if (source.StartsWith(AgentJobEventPersistence.SourcePrefix, StringComparison.Ordinal))
        {
            var existing = await db.AgentJobEvents
                .AsNoTracking()
                .Where(r => r.Source == source && r.EventId == envelope.Id)
                .Select(r => (long?)r.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return;
            var nextId = await NextAgentJobIdAsync(db, source, ct);
            db.AgentJobEvents.Add(new AgentJobEventRow
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
            return;
        }

        if (IngressEventPersistence.IsIngressSource(source))
        {
            var nextId = await NextIngressIdAsync(db, source, ct);
            db.IngressEvents.Add(new IngressEventRow
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
            return;
        }

        if (IssueEventPersistence.IsIssueSource(source))
        {
            var nextSequence = await NextIssueSequenceAsync(db, source, ct);
            db.IssueEvents.Add(new IssueEventRow
            {
                Id = nextSequence,
                Source = source,
                TimelineSource = source,
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? JsonDocument.Parse("null").RootElement,
                ExtensionsJson = SerializeExtensions(envelope.Extensions),
            });
            return;
        }

        if (EpicEventPersistence.IsEpicSource(source))
        {
            var nextSequence = await NextEpicSequenceAsync(db, source, ct);
            db.EpicEvents.Add(new EpicEventRow
            {
                Id = nextSequence,
                Source = source,
                TimelineSource = source,
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? JsonDocument.Parse("null").RootElement,
                ExtensionsJson = SerializeExtensions(envelope.Extensions),
            });
            return;
        }

        if (WorkspaceEventPersistence.IsWorkspaceSource(source))
        {
            var nextSequence = await NextWorkspaceSequenceAsync(db, source, ct);
            db.WorkspaceEvents.Add(new WorkspaceEventRow
            {
                Id = nextSequence,
                Source = source,
                TimelineSource = source,
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? JsonDocument.Parse("null").RootElement,
                ExtensionsJson = SerializeExtensions(envelope.Extensions),
            });
            return;
        }

        var workflowNextId = await NextWorkflowIdAsync(db, source, ct);
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

    public async Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default)
    {
        var source = IssueEventPersistence.IssueSource(projectId, issueNumber);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.IssueEvents.AsNoTracking()
            .Where(e => e.TimelineSource == source)
            .ToListAsync(ct);

        return rows.OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Source)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Source)
            .ThenBy(e => e.Id)
            .Select(ToIssueStored)
            .ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default)
    {
        var source = EpicEventPersistence.EpicSource(projectId, epicNumber);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.EpicEvents.AsNoTracking()
            .Where(e => e.TimelineSource == source)
            .ToListAsync(ct);

        return rows.OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Source)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Source)
            .ThenBy(e => e.Id)
            .Select(ToEpicStored)
            .ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default)
    {
        var source = AgentSessionEventPersistence.AgentSessionSource(sessionId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessionEvents.AsNoTracking()
            .Where(e => e.Source == source)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        return rows.Select(ToAgentSessionStored).ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default)
    {
        var source = AgentJobEventPersistence.AgentJobSource(agentJobId);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentJobEvents.AsNoTracking()
            .Where(e => e.Source == source)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        return rows.Select(ToAgentJobStored).ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListWorkspaceEventsAsync(string projectId, string name, int limit = 200, CancellationToken ct = default)
    {
        var source = WorkspaceEventPersistence.WorkspaceSource(projectId, name);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkspaceEvents.AsNoTracking()
            .Where(e => e.TimelineSource == source)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        return rows.Select(ToWorkspaceStored).ToList();
    }

    public async Task MarkDispatchedAsync(
        EventOrigin origin,
        string source,
        long id,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await SetDispatchedAsync(db, origin, source, id, dispatchedAt, ct);
        await db.SaveChangesAsync(ct);
    }

    internal static async Task SetDispatchedAsync(
        MohistDbContext db,
        EventOrigin origin,
        string source,
        long id,
        DateTimeOffset dispatchedAt,
        CancellationToken ct)
    {
        switch (origin)
        {
            case EventOrigin.AgentSession:
            {
                var row = await db.AgentSessionEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
                if (row is null)
                    throw new InvalidOperationException($"Agent session event '{source}'/{id} was not found.");
                row.DispatchedAt = dispatchedAt;
                break;
            }
            case EventOrigin.AgentJob:
            {
                var row = await db.AgentJobEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
                if (row is null)
                    throw new InvalidOperationException($"Agent job event '{source}'/{id} was not found.");
                row.DispatchedAt = dispatchedAt;
                break;
            }
            case EventOrigin.Ingress:
            {
                var row = await db.IngressEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
                if (row is null)
                    throw new InvalidOperationException($"Ingress event '{source}'/{id} was not found.");
                row.DispatchedAt = dispatchedAt;
                break;
            }
            case EventOrigin.Issue:
            {
                var row = await db.IssueEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
                if (row is null)
                    throw new InvalidOperationException($"Issue event '{source}'/{id} was not found.");
                row.DispatchedAt = dispatchedAt;
                break;
            }
            case EventOrigin.Epic:
            {
                var row = await db.EpicEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
                if (row is null)
                    throw new InvalidOperationException($"Epic event '{source}'/{id} was not found.");
                row.DispatchedAt = dispatchedAt;
                break;
            }
            case EventOrigin.Workspace:
            {
                var row = await db.WorkspaceEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
                if (row is null)
                    throw new InvalidOperationException($"Workspace event '{source}'/{id} was not found.");
                row.DispatchedAt = dispatchedAt;
                break;
            }
            case EventOrigin.WorkflowRun:
            {
                var row = await db.WorkflowRunEvents.FirstOrDefaultAsync(e => e.Source == source && e.Id == id, ct);
                if (row is null)
                    throw new InvalidOperationException($"Workflow run event '{source}'/{id} was not found.");
                row.DispatchedAt = dispatchedAt;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown event origin.");
        }
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
            UNION ALL
            SELECT 'AgentSession' AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "AgentSessionEvents" WHERE "DispatchedAt" IS NULL
            UNION ALL
            SELECT 'AgentJob' AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "AgentJobEvents" WHERE "DispatchedAt" IS NULL
            UNION ALL
            SELECT 'Ingress' AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "IngressEvents" WHERE "DispatchedAt" IS NULL
            UNION ALL
            SELECT 'Workspace' AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "WorkspaceEvents" WHERE "DispatchedAt" IS NULL
            ORDER BY "Source", "Id"
            LIMIT @limit
            """;

        var parameter = new Microsoft.Data.Sqlite.SqliteParameter("@limit", limit);
        var rows = await db.Database
            .SqlQueryRaw<UndeliveredSqlRow>(sql, parameter)
            .ToListAsync(ct);

        return rows.Select(ToUndeliveredEvent).ToList();
    }

    public async Task<IReadOnlyList<PendingStream>> ListPendingStreamsAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        const string sql = """
            SELECT "Origin", "Source", MIN("Time") AS "OldestPendingTime"
            FROM (
                SELECT 'WorkflowRun' AS "Origin", "Source", "Time" FROM "WorkflowRunEvents" WHERE "DispatchedAt" IS NULL
                UNION ALL
                SELECT 'Issue' AS "Origin", "Source", "Time" FROM "IssueEvents" WHERE "DispatchedAt" IS NULL
                UNION ALL
                SELECT 'Epic' AS "Origin", "Source", "Time" FROM "EpicEvents" WHERE "DispatchedAt" IS NULL
                UNION ALL
                SELECT 'AgentSession' AS "Origin", "Source", "Time" FROM "AgentSessionEvents" WHERE "DispatchedAt" IS NULL
                UNION ALL
                SELECT 'AgentJob' AS "Origin", "Source", "Time" FROM "AgentJobEvents" WHERE "DispatchedAt" IS NULL
                UNION ALL
                SELECT 'Ingress' AS "Origin", "Source", "Time" FROM "IngressEvents" WHERE "DispatchedAt" IS NULL
                UNION ALL
                SELECT 'Workspace' AS "Origin", "Source", "Time" FROM "WorkspaceEvents" WHERE "DispatchedAt" IS NULL
            )
            GROUP BY "Origin", "Source"
            ORDER BY "OldestPendingTime"
            LIMIT @limit
            """;

        var parameter = new Microsoft.Data.Sqlite.SqliteParameter("@limit", limit);
        var rows = await db.Database
            .SqlQueryRaw<PendingStreamSqlRow>(sql, parameter)
            .ToListAsync(ct);

        return rows
            .Select(r => new PendingStream(ParseOrigin(r.Origin), r.Source, r.OldestPendingTime))
            .ToList();
    }

    public async Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredByStreamAsync(
        EventOrigin origin,
        string source,
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Raw SQL like ListUndeliveredAsync: the LINQ projector cannot
        // translate the JsonElement Data column through the generic
        // envelope interface.
        var sql = $"""
            SELECT @origin AS "Origin", "Id", "Source", "EventId", "Type", "Time",
                   "SpecVersion", "Subject", "DataContentType", "Data", "ExtensionsJson"
            FROM "{TableName(origin)}"
            WHERE "DispatchedAt" IS NULL AND "Source" = @source
            ORDER BY "Id"
            LIMIT @limit
            """;

        var rows = await db.Database
            .SqlQueryRaw<UndeliveredSqlRow>(
                sql,
                new Microsoft.Data.Sqlite.SqliteParameter("@origin", origin.ToString()),
                new Microsoft.Data.Sqlite.SqliteParameter("@source", source),
                new Microsoft.Data.Sqlite.SqliteParameter("@limit", limit))
            .ToListAsync(ct);

        return rows
            .Select(r => new UndeliveredEvent(
                origin,
                r.Id,
                r.Source,
                r.EventId,
                r.Type,
                r.Time,
                r.SpecVersion,
                r.Subject,
                r.DataContentType,
                ParseJsonElement(r.Data),
                r.ExtensionsJson))
            .ToList();
    }

    public async Task MarkDispatchedRangeAsync(
        EventOrigin origin,
        string source,
        IReadOnlyList<long> ids,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await Task.FromResult(origin switch
        {
            EventOrigin.WorkflowRun => MarkRangeAsync(db.WorkflowRunEvents, source, ids, dispatchedAt, ct),
            EventOrigin.Issue => MarkRangeAsync(db.IssueEvents, source, ids, dispatchedAt, ct),
            EventOrigin.Epic => MarkRangeAsync(db.EpicEvents, source, ids, dispatchedAt, ct),
            EventOrigin.AgentSession => MarkRangeAsync(db.AgentSessionEvents, source, ids, dispatchedAt, ct),
            EventOrigin.AgentJob => MarkRangeAsync(db.AgentJobEvents, source, ids, dispatchedAt, ct),
            EventOrigin.Ingress => MarkRangeAsync(db.IngressEvents, source, ids, dispatchedAt, ct),
            EventOrigin.Workspace => MarkRangeAsync(db.WorkspaceEvents, source, ids, dispatchedAt, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown event origin."),
        });
    }

    private static Task MarkRangeAsync<T>(
        IQueryable<T> rows,
        string source,
        IReadOnlyList<long> ids,
        DateTimeOffset dispatchedAt,
        CancellationToken ct)
        where T : class, IEventEnvelopeRow =>
        rows
            .Where(e => e.Source == source && ids.Contains(e.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.DispatchedAt, dispatchedAt),
                ct);
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

    private static StoredCloudEvent ToAgentSessionStored(AgentSessionEventRow row) =>
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

    private static StoredCloudEvent ToAgentJobStored(AgentJobEventRow row) =>
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

    private static StoredCloudEvent ToWorkspaceStored(WorkspaceEventRow row) =>
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

    private static string SerializeExtensions(IReadOnlyDictionary<string, string>? extensions) =>
        extensions is null ? "{}" : JsonSerializer.Serialize(extensions, CloudEvent.JsonOptions);

    private static IReadOnlyDictionary<string, string> DeserializeExtensions(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, CloudEvent.JsonOptions)
            ?? new Dictionary<string, string>();

    private static Task<long> NextWorkflowIdAsync(MohistDbContext db, string source, CancellationToken ct) =>
        NextIdAsync(db.WorkflowRunEvents, source, ct);

    private static Task<long> NextIssueSequenceAsync(MohistDbContext db, string source, CancellationToken ct) =>
        NextIdAsync(db.IssueEvents, source, ct);

    private static Task<long> NextEpicSequenceAsync(MohistDbContext db, string source, CancellationToken ct) =>
        NextIdAsync(db.EpicEvents, source, ct);

    private static Task<long> NextAgentSessionIdAsync(MohistDbContext db, string source, CancellationToken ct) =>
        NextIdAsync(db.AgentSessionEvents, source, ct);

    private static Task<long> NextAgentJobIdAsync(MohistDbContext db, string source, CancellationToken ct) =>
        NextIdAsync(db.AgentJobEvents, source, ct);

    private static Task<long> NextIngressIdAsync(MohistDbContext db, string source, CancellationToken ct) =>
        NextIdAsync(db.IngressEvents, source, ct);

    private static Task<long> NextWorkspaceSequenceAsync(MohistDbContext db, string source, CancellationToken ct) =>
        NextIdAsync(db.WorkspaceEvents, source, ct);

    private static async Task<long> NextIdAsync<T>(DbSet<T> set, string source, CancellationToken ct)
        where T : class, IEventRow
    {
        var localMax = set.Local
            .Where(r => r.Source == source)
            .Select(r => (long?)r.Id)
            .DefaultIfEmpty()
            .Max();
        var committedMax = await set
            .Where(r => r.Source == source)
            .Select(r => (long?)r.Id)
            .MaxAsync(ct);
        return Math.Max(localMax ?? 0, committedMax ?? 0) + 1;
    }
private sealed class PendingStreamSqlRow
    {
        public string Origin { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTimeOffset OldestPendingTime { get; set; }
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
        "AgentSession" => EventOrigin.AgentSession,
        "AgentJob" => EventOrigin.AgentJob,
        "Ingress" => EventOrigin.Ingress,
        "Workspace" => EventOrigin.Workspace,
        _ => throw new InvalidOperationException($"Unknown event origin '{text}'."),
    };

    private static string TableName(EventOrigin origin) => origin switch
    {
        EventOrigin.WorkflowRun => "WorkflowRunEvents",
        EventOrigin.Issue => "IssueEvents",
        EventOrigin.Epic => "EpicEvents",
        EventOrigin.AgentSession => "AgentSessionEvents",
        EventOrigin.AgentJob => "AgentJobEvents",
        EventOrigin.Ingress => "IngressEvents",
        EventOrigin.Workspace => "WorkspaceEvents",
        _ => throw new InvalidOperationException($"Unknown event origin '{origin}'."),
    };

    private static JsonElement ParseJsonElement(string json) =>
        JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json).RootElement.Clone();
}
