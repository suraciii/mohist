using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

public sealed class AgentSessionResolver
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
