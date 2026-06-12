using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Sessions;

public interface IAgentSessionStore : IStateStore<AgentSession>
{
    Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default);
}

public class AgentSessionStore : IAgentSessionStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentSessionStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
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
        return rows.Select(AgentSessionJson.Deserialize).OfType<AgentSession>().ToList();
    }

    public async Task SaveAsync(string key, AgentSession state)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await StageSessionAsync(db, key, state);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageSessionAsync(db, key, state, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task StageSessionAsync(MohistDbContext db, string key, AgentSession state, CancellationToken ct = default)
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
        await SyncLabelsAsync(db, key, state.Metadata.Labels, ct);
    }

    private static async Task SyncLabelsAsync(
        MohistDbContext db,
        string sessionId,
        IReadOnlyDictionary<string, string>? labels,
        CancellationToken ct)
    {
        var existing = await db.AgentSessionLabels
            .Where(label => label.SessionId == sessionId)
            .ToListAsync(ct);
        if (existing.Count > 0)
            db.AgentSessionLabels.RemoveRange(existing);
        if (labels is null) return;
        foreach (var (key, value) in labels)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            db.AgentSessionLabels.Add(new AgentSessionLabelRow
            {
                SessionId = sessionId,
                Key = key,
                Value = value,
            });
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

}

public static class AgentSessionJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AgentSession? Deserialize(AgentSessionRow row)
    {
        try
        {
            var session = JsonSerializer.Deserialize<AgentSession>(row.State, JsonOptions);
            return session is null ? null : Normalize(session, row);
        }
        catch
        {
            return null;
        }
    }

    public static AgentSessionRow ToRow(AgentSession session, DateTime updatedAt) => new()
    {
        Id = session.Id,
        State = JsonSerializer.Serialize(session, JsonOptions),
        RunnerId = session.Runtime.RunnerId,
        AgentSessionId = session.Status.AgentRuntimeSessionId,
        Status = AgentSessionStatusNames.ToName(session.Status.Phase),
        CreatedAt = session.Status.CreatedAt,
        LastDataAt = session.Status.LastDataAt,
        CompletedAt = null,
        UpdatedAt = updatedAt,
    };

    private static AgentSession Normalize(AgentSession session, AgentSessionRow row)
    {
        if (string.IsNullOrWhiteSpace(session.Id))
            throw new InvalidOperationException("AgentSession state is missing required fields.");
        if (session.Runtime is null)
            session.Runtime = new AgentSessionRuntime(row.RunnerId ?? string.Empty, "opencode", null);
        else if (string.IsNullOrWhiteSpace(session.Runtime.AgentRuntime))
            session.Runtime = session.Runtime with { AgentRuntime = "opencode" };
        if (session.Status.CreatedAt == default)
            throw new InvalidOperationException("AgentSession state is missing CreatedAt.");
        var phase = !string.IsNullOrWhiteSpace(session.Status.AgentRuntimeSessionId) || !string.IsNullOrWhiteSpace(row.AgentSessionId)
            ? AgentSessionStatus.Bound
            : AgentSessionStatus.Opened;
        session.Status = session.Status with
        {
            Phase = phase,
            AgentRuntimeSessionId = session.Status.AgentRuntimeSessionId ?? row.AgentSessionId,
            LastDataAt = session.Status.LastDataAt ?? row.LastDataAt,
            UsageSummary = session.Status.UsageSummary ?? new AgentUsageSummary()
        };
        return session;
    }
}
