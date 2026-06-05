using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Data.Events;

internal static class AgentSessionEventPersistence
{
    public const string SpecVersion = "1.0";

    public static async Task<IReadOnlyList<StagedAgentSessionEvent>> StageAsync(
        MohistDbContext db,
        string sessionId,
        IReadOnlyList<AgentSessionEvent> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0) return [];

        var source = AgentSessionSource(sessionId);
        var nextId = (await db.Events
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync(ct) ?? 0) + 1;
        var staged = new List<StagedAgentSessionEvent>(events.Count);

        foreach (var payload in events)
        {
            var type = AgentSessionEventSerializer.Type(payload);
            var row = new EventRow
            {
                Source = source,
                Id = nextId++,
                Data = AgentSessionEventSerializer.ToData(payload),
                AgentSessionEvent = payload,
            };

            db.Events.Add(row);
            staged.Add(new StagedAgentSessionEvent(row, type, payload));
        }

        return staged;
    }

    public static AgentSessionDomainEventDto ToDto(StagedAgentSessionEvent staged) => new(
        staged.Row.Id,
        staged.Row.Source,
        staged.Type,
        staged.Payload,
        staged.Row.Time,
        SpecVersion);

    public static AgentSessionDomainEventDto ToDto(EventRow row, string type, string specVersion) => new(
        row.Id,
        row.Source,
        type,
        row.AgentSessionEvent ?? AgentSessionEventSerializer.FromData(type, row.Data),
        row.Time,
        specVersion);

    public static string AgentSessionSource(string sessionId) => $"/agent-sessions/{sessionId}";
}

public sealed record StagedAgentSessionEvent(EventRow Row, string Type, AgentSessionEvent Payload);
