using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.PublicApi;
using Orleans;

namespace Mohist.Server.Infrastructure.Data.Sessions;

public interface IAgentSessionStore : IStateStore<AgentSession>
{
    Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(string runnerId, CancellationToken ct = default);
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default);
}

/// <summary>
/// Explicit retention boundary for a deleted Session's public stream. The
/// delete operation closes the stream; this operation physically purges its
/// tombstone and retained public rows after the cursor-retention window.
/// </summary>
public interface IAgentSessionStreamRetention
{
    Task PurgeDeletedAsync(string sessionId, CancellationToken ct = default);
}

public sealed record AgentSessionReconcileBinding(
    string SessionId,
    string Runtime,
    string RuntimeSessionId,
    string WorkDir);

public class AgentSessionStore : IAgentSessionStore, IAgentSessionStreamRetention
{
    private const string SpecVersion = "1.0";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventStore _eventStore;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<AgentSessionStore> _log;
    private readonly IBackgroundTaskLauncher _backgroundTasks;
    private readonly IPublicProjectionNudge? _publicProjectionNudge;

    public AgentSessionStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventStore eventStore,
        IGrainFactory grainFactory,
        ILogger<AgentSessionStore> log,
        IBackgroundTaskLauncher backgroundTasks,
        IPublicProjectionNudge? publicProjectionNudge = null)
    {
        _dbFactory = dbFactory;
        _eventStore = eventStore;
        _grainFactory = grainFactory;
        _log = log;
        _backgroundTasks = backgroundTasks;
        _publicProjectionNudge = publicProjectionNudge;
    }

    public async Task<AgentSession?> LoadAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key);
        return row is null ? null : AgentSessionJson.Deserialize(row);
    }

    public async Task<IReadOnlyList<AgentSession>> ListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.AgentSessions.AsNoTracking().ToListAsync();
        return rows.Select(r => AgentSessionJson.Deserialize(r)).OfType<AgentSession>().ToList();
    }

    public async Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(
        string runnerId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions
            .AsNoTracking()
            .Where(row => row.RunnerId == runnerId && row.AgentSessionId != null)
            .ToListAsync(ct);

        return rows
            .Select(row => AgentSessionJson.Deserialize(row))
            .OfType<AgentSession>()
            .Where(session => session.Status.Activity != AgentSessionActivity.Idle)
            .Select(session => new AgentSessionReconcileBinding(
                session.Id,
                session.Runtime.Runtime ?? string.Empty,
                session.Status.AgentRuntimeSessionId ?? string.Empty,
                session.Runtime.WorkDir ?? string.Empty))
            .Where(binding => binding.Runtime.Length > 0
                && binding.RuntimeSessionId.Length > 0
                && binding.WorkDir.Length > 0)
            .ToArray();
    }

    public async Task SaveAsync(string key, AgentSession state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var previous = await StageSessionAsync(db, key, state);
        await StageLifecycleTransitionsAsync(db, key, previous, state);
        await db.SaveChangesAsync();
        PokeDispatcherBestEffort();
    }

    public async Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default)
    {
        var source = AgentSessionEventPersistence.AgentSessionSource(state.Id);
        var subject = state.Id;
        IReadOnlyDictionary<string, string>? extensions = null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var previous = await StageSessionAsync(db, key, state, ct);
            await StageLifecycleTransitionsAsync(db, key, previous, state, ct);
            foreach (var evt in events)
            {
                if (evt is null) continue;
                extensions ??= AgentSessionLineage.BuildExtensions(state);
                var envelope = ToCloudEvent(evt, source, subject, extensions);
                await _eventStore.AppendAsync(db, envelope, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }

        PokeDispatcherBestEffort();
    }

    private void PokeDispatcherBestEffort()
    {
        EventDispatcherPoke.PokeAfterCommit(_grainFactory, _log, nameof(AgentSessionStore), _backgroundTasks);
        // Best-effort latency nudge for the public execution projector;
        // its timer sweep recovers anything lost here.
        try
        {
            _publicProjectionNudge?.Nudge();
        }
        catch
        {
            // A nudge is advisory only.
        }
    }

    private async Task<AgentSession?> StageSessionAsync(
        MohistDbContext db,
        string key,
        AgentSession state,
        CancellationToken ct = default)
    {
        var row = AgentSessionJson.ToRow(state, DateTime.UtcNow);
        row.Id = key;
        var existing = await db.AgentSessions.FindAsync([key], ct);
        var previous = existing is null ? null : AgentSessionJson.Deserialize(existing);
        if (existing is null)
        {
            db.AgentSessions.Add(row);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(row);
        }

        return previous;
    }

    private async Task StageLifecycleTransitionsAsync(
        MohistDbContext db,
        string sessionId,
        AgentSession? previous,
        AgentSession current,
        CancellationToken ct = default)
    {
        var jobRows = await db.AgentJobs.AsNoTracking()
            .Where(row => row.AgentSessionId == sessionId)
            .ToListAsync(ct);
        var jobs = jobRows
            .Select(ToLifecycleJob)
            .ToList();

        foreach (var transition in AgentSessionLifecycleHistory.Derive(previous, current, jobs, DateTimeOffset.UtcNow))
        {
            db.AgentSessionLifecycleTransitions.Add(new AgentSessionLifecycleTransitionRow
            {
                SessionId = sessionId,
                SourceTransition = transition.SourceTransition,
                EventType = transition.EventType,
                AnchorKind = transition.AnchorKind,
                AnchorId = transition.AnchorId,
                SnapshotJson = transition.SnapshotJson,
                OccurredAt = transition.OccurredAt,
            });
        }
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await db.AgentSessions.FindAsync(key);
        if (session is null)
        {
            await transaction.CommitAsync();
            return;
        }

        var stream = await db.PublicStreamStates.FindAsync(key);
        if (stream is not null)
        {
            stream.Closed = true;
            stream.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.AgentSessions.Remove(session);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task PurgeDeletedAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var stream = await db.PublicStreamStates.FirstOrDefaultAsync(row => row.SessionId == sessionId, ct);
        if (stream is null)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        if (!stream.Closed)
        {
            throw new InvalidOperationException(
                $"Cannot purge the public stream for Session {sessionId} before it is closed.");
        }

        var events = await db.PublicSessionEvents
            .Where(row => row.SessionId == sessionId)
            .ToListAsync(ct);
        var snapshots = await db.PublicExecutionSnapshots
            .Where(row => row.SessionId == sessionId)
            .ToListAsync(ct);
        var checkpoints = await db.PublicProjectionCheckpoints
            .Where(row => row.SourceKey == sessionId
                && (row.Feed == PublicProjectionFeeds.AgentSessions
                    || row.Feed == PublicProjectionFeeds.AgentSessionEvents
                    || row.Feed == PublicProjectionFeeds.AgentSessionLifecycle))
            .ToListAsync(ct);
        var lifecycle = await db.AgentSessionLifecycleTransitions
            .Where(row => row.SessionId == sessionId)
            .ToListAsync(ct);

        db.PublicSessionEvents.RemoveRange(events);
        db.PublicExecutionSnapshots.RemoveRange(snapshots);
        db.PublicProjectionCheckpoints.RemoveRange(checkpoints);
        db.AgentSessionLifecycleTransitions.RemoveRange(lifecycle);
        db.PublicStreamStates.Remove(stream);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static AgentSessionLifecycleJob ToLifecycleJob(AgentJobRow row)
    {
        using var document = JsonDocument.Parse(row.State);
        var root = document.RootElement;
        var input = Property(root, "input");
        var terminal = Property(root, "terminalResult");
        return new AgentSessionLifecycleJob(
            row.JobKey,
            StringProperty(root, "status") ?? string.Empty,
            StringProperty(input, "projectId") ?? row.ProjectId,
            StringProperty(input, "agentId") ?? row.AgentId,
            row.AgentSessionId,
            row.InitialInputId,
            row.InitialTurnId,
            TimestampProperty(root, "submittedAt"),
            ParseTimestamp(row.ReadySince),
            TimestampProperty(root, "runningSince"),
            TimestampProperty(root, "terminalAt"),
            StringProperty(root, "waitingReason"),
            StringProperty(terminal, "status"),
            StringProperty(terminal, "message"),
            StringProperty(terminal, "output"),
            StringProperty(terminal, "failureReason"),
            IntProperty(terminal, "exitCode"));
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
                ? value
                : default;

    private static string? StringProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static DateTimeOffset? TimestampProperty(JsonElement element, string name) =>
        ParseTimestamp(StringProperty(element, name));

    private static int? IntProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static DateTimeOffset? ParseTimestamp(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : DateTimeOffset.Parse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);

    private static CloudEvent ToCloudEvent(AgentSessionEvent evt, string source, string subject, IReadOnlyDictionary<string, string> extensions)
    {
        var type = AgentSessionEventSerializer.BusType(evt);
        var data = AgentSessionEventSerializer.ToData(evt);
        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: data,
            subject: subject,
            specVersion: SpecVersion,
            extensions: extensions);
    }
}

public static class AgentSessionJson
{
    public static readonly JsonSerializerOptions JsonOptions = Mohist.Server.Infrastructure.JSON.Options;

    public static AgentSession? Deserialize(AgentSessionRow row, ILogger? logger = null)
    {
        try
        {
            var session = JsonSerializer.Deserialize<AgentSession>(row.State, JsonOptions);
            if (session is null) return null;
            ApplyColumnDefaults(session, row);
            session.ValidateState(allowLegacySource: true);
            return session;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize AgentSession row {AgentSessionId}; skipping row", row.Id);
            return null;
        }
    }

    public static AgentSessionRow ToRow(AgentSession session, DateTime updatedAt) => new()
    {
        Id = session.Id,
        State = JsonSerializer.Serialize(session, JsonOptions),
        RunnerId = session.Runtime.RunnerId,
        AgentSessionId = session.Status.AgentRuntimeSessionId,
        Status = session.Status.AgentRuntimeSessionId is null ? "opened" : "bound",
        CreatedAt = session.Status.CreatedAt,
        LastDataAt = session.Status.LastDataAt,
        ParentLinkEdgeId = session.ParentLink?.EdgeId,
        ParentSessionId = session.ParentLink?.ParentSessionId,
        ParentAgentId = session.ParentLink?.ParentAgentId,
        ChildLaunchJobId = session.ParentLink?.ChildLaunchJobId,
        ParentLinkState = session.ParentLink?.State.ToString().ToLowerInvariant(),
        ParentLinkAttachedRevision = session.ParentLink?.AttachedRevision,
        ParentLinkAttachedAt = session.ParentLink?.AttachedAt.ToString("O"),
        ParentLinkDetachedRevision = session.ParentLink?.DetachedRevision,
        ParentLinkDetachedAt = session.ParentLink?.DetachedAt?.ToString("O"),
        LaunchVisibility = session.LaunchVisibility.ToString().ToLowerInvariant(),
    };

    private static void ApplyColumnDefaults(AgentSession session, AgentSessionRow row)
    {
        session.Status = session.Status with
        {
            AgentRuntimeSessionId = session.Status.AgentRuntimeSessionId ?? row.AgentSessionId,
            LastDataAt = session.Status.LastDataAt ?? row.LastDataAt,
            UsageSummary = session.Status.UsageSummary ?? new AgentUsageSummary()
        };
        if (!Enum.TryParse<AgentLaunchVisibility>(row.LaunchVisibility, true, out var visibility))
            visibility = AgentLaunchVisibility.Visible;
        session.LaunchVisibility = visibility;
    }
}
