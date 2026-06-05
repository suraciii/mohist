using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Data.Events;

internal static class AgentSessionEventPersistence
{
    public const string SpecVersion = "1.0";

    public static async Task StageAsync(
        MohistDbContext db,
        string sessionId,
        IReadOnlyList<AgentSessionEvent> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0) return;

        var source = AgentSessionSource(sessionId);
        var nextId = (await db.Events
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync(ct) ?? 0) + 1;

        foreach (var payload in events)
        {
            var row = new EventRow
            {
                Source = source,
                Id = nextId++,
                Data = AgentSessionEventSerializer.ToData(payload),
                AgentSessionEvent = payload,
            };

            db.Events.Add(row);
        }
    }

    public static string AgentSessionSource(string sessionId) => $"/agent-sessions/{sessionId}";
}
