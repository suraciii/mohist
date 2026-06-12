using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public enum AgentSessionQueryOrder
{
    CreatedAscending,
    CreatedDescending,
}

public sealed class AgentSessionQuery
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentSessionQuery(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AgentSessionRecord?> FirstByLabelsAsync(
        IReadOnlyDictionary<string, string> labels,
        AgentSessionQueryOrder order = AgentSessionQueryOrder.CreatedAscending,
        CancellationToken ct = default)
    {
        var records = await ListByLabelsAsync(labels, order, limit: 1, ct);
        return records.FirstOrDefault();
    }

    public async Task<IReadOnlyList<AgentSessionRecord>> ListByLabelsAsync(
        IReadOnlyDictionary<string, string> labels,
        AgentSessionQueryOrder order = AgentSessionQueryOrder.CreatedAscending,
        int? limit = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = QueryRowsByLabels(db, labels);
        query = order == AgentSessionQueryOrder.CreatedDescending
            ? query.OrderByDescending(session => session.CreatedAt)
            : query.OrderBy(session => session.CreatedAt);
        if (limit is > 0)
            query = query.Take(limit.Value);

        var rows = await query.ToListAsync(ct);
        return await ToRecordsAsync(db, rows, ct);
    }

    public async Task<IReadOnlyList<AgentSessionRecord>> ListByIdsAsync(
        IReadOnlyList<string> sessionIds,
        CancellationToken ct = default)
    {
        if (sessionIds.Count == 0) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(session => sessionIds.Contains(session.Id))
            .OrderBy(session => session.CreatedAt)
            .ToListAsync(ct);
        return await ToRecordsAsync(db, rows, ct);
    }

    private static IQueryable<AgentSessionRow> QueryRowsByLabels(
        MohistDbContext db,
        IReadOnlyDictionary<string, string> labels)
    {
        var filters = labels
            .Where(label => !string.IsNullOrWhiteSpace(label.Key) && !string.IsNullOrWhiteSpace(label.Value))
            .ToArray();
        if (filters.Length == 0)
            return db.AgentSessions.AsNoTracking().Where(_ => false);

        IQueryable<AgentSessionRow> query = db.AgentSessions.AsNoTracking();
        foreach (var (key, value) in filters)
        {
            query = query.Where(session => db.AgentSessionLabels.Any(label =>
                label.SessionId == session.Id
                && label.Key == key
                && label.Value == value));
        }

        return query;
    }

    private static async Task<IReadOnlyList<AgentSessionRecord>> ToRecordsAsync(
        MohistDbContext db,
        IReadOnlyList<AgentSessionRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var ids = rows.Select(row => row.Id).ToArray();
        var labels = await db.AgentSessionLabels.AsNoTracking()
            .Where(label => ids.Contains(label.SessionId))
            .ToListAsync(ct);
        var labelsBySession = labels
            .GroupBy(label => label.SessionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group.ToDictionary(label => label.Key, label => label.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var result = new List<AgentSessionRecord>();
        foreach (var row in rows)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is null) continue;
            labelsBySession.TryGetValue(row.Id, out var sessionLabels);
            result.Add(new AgentSessionRecord(row, session, sessionLabels ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        return result;
    }
}

public sealed record AgentSessionRecord(
    AgentSessionRow Row,
    AgentSession Session,
    IReadOnlyDictionary<string, string> Labels)
{
    public string? Label(string key) => Labels.TryGetValue(key, out var value) ? value : null;
}
