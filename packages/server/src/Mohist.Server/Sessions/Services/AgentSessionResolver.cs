using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Stable AgentSession id derived from the routing trigger identity
    /// (project id, triggering event id, routing rule id). Identical
    /// inputs always produce the same id; the AgentLauncher and the
    /// routed preflight-failure path both rely on this so redelivery
    /// reuses one session grain.
    /// </summary>
    public string StableSessionId(string projectId, string eventId, string ruleId) =>
        StableId("agent-session", BuildTriggerIdentity(projectId, eventId, ruleId));

    /// <summary>
    /// Stable AgentJob grain key derived from the same trigger identity
    /// as <see cref="StableSessionId"/>. Routed preflight-failure paths
    /// mint this so the durable AgentJob grain owns the canonical
    /// preflight-failed plan.
    /// </summary>
    public string StableJobKey(string projectId, string eventId, string ruleId) =>
        StableId("agent-job-trigger", BuildTriggerIdentity(projectId, eventId, ruleId));

    private static string BuildTriggerIdentity(string projectId, string eventId, string ruleId) =>
        $"{projectId}\n{eventId}\n{ruleId}";

    private static string StableId(string prefix, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
