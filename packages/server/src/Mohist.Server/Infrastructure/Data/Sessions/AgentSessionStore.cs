using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Orleans;

namespace Mohist.Server.Infrastructure.Data.Sessions;

public interface IAgentSessionStore : IStateStore<AgentSession>
{
    Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(string runnerId, CancellationToken ct = default);
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default);
}

public sealed record AgentSessionReconcileBinding(
    string SessionId,
    string Runtime,
    string RuntimeSessionId,
    string WorkDir);

public class AgentSessionStore : IAgentSessionStore
{
    private const string SpecVersion = "1.0";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventStore _eventStore;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<AgentSessionStore> _log;
    private readonly IBackgroundTaskLauncher _backgroundTasks;

    public AgentSessionStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventStore eventStore,
        IGrainFactory grainFactory,
        ILogger<AgentSessionStore> log,
        IBackgroundTaskLauncher backgroundTasks)
    {
        _dbFactory = dbFactory;
        _eventStore = eventStore;
        _grainFactory = grainFactory;
        _log = log;
        _backgroundTasks = backgroundTasks;
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
        await StageSessionAsync(db, key, state);
        await db.SaveChangesAsync();
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
            await StageSessionAsync(db, key, state, ct);
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

    private void PokeDispatcherBestEffort() =>
        EventDispatcherPoke.PokeAfterCommit(_grainFactory, _log, nameof(AgentSessionStore), _backgroundTasks);

    private async Task StageSessionAsync(MohistDbContext db, string key, AgentSession state, CancellationToken ct = default)
    {
        var row = AgentSessionJson.ToRow(state, DateTime.UtcNow);
        row.Id = key;
        var existing = await db.AgentSessions.FindAsync([key], ct);
        if (existing is null)
        {
            db.AgentSessions.Add(row);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(row);
        }
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.AgentSessions.FindAsync(key);
        if (session is not null)
        {
            db.AgentSessions.Remove(session);
            await db.SaveChangesAsync();
        }
    }

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
