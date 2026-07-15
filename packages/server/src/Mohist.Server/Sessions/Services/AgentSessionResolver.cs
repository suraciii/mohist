using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

public sealed class AgentSessionResolver : IScopedService
{
    private readonly AgentSessionQuery _query;
    private readonly IGrainFactory _grains;

    public AgentSessionResolver(AgentSessionQuery query, IGrainFactory grains)
    {
        _query = query;
        _grains = grains;
    }

    public async Task<string?> ResolveByLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        var record = await _query.FirstByLabelsAsync(labels, ct: ct);
        return record?.Session.Id;
    }

    public async Task<string?> ResolveCanonicalIdAsync(string projectId, string sessionId, CancellationToken ct = default)
    {
        var records = await _query.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        return record is not null
            && string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal)
            ? record.Session.Id
            : null;
    }

    public async Task<AgentSessionInfo?> GetByLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        var sessionId = await ResolveByLabelsAsync(labels, ct);
        if (sessionId is null) return null;
        return await _grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
    }

    public IAgentSessionGrain GetGrain(string sessionId) =>
        _grains.GetGrain<IAgentSessionGrain>(sessionId);

    public string NewSessionId() => Guid.NewGuid().ToString("N");
}
