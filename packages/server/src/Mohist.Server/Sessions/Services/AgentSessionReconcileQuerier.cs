using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Sessions.Services;

public sealed class AgentSessionReconcileQuerier
{
    private readonly IAgentSessionStore _store;

    public AgentSessionReconcileQuerier(IAgentSessionStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<AgentSessionReconcileItem>> ListByRunnerAsync(
        string runnerId,
        CancellationToken ct = default)
    {
        var bindings = await _store.ListByRunnerForReconcileAsync(runnerId, ct);
        return bindings.Select(binding => new AgentSessionReconcileItem(
            binding.SessionId,
            binding.Runtime,
            binding.RuntimeSessionId,
            binding.WorkDir)).ToArray();
    }
}

public sealed record AgentSessionReconcileItem(
    string SessionId,
    string Runtime,
    string RuntimeSessionId,
    string WorkDir);
