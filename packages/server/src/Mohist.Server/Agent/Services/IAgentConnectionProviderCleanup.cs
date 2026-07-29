namespace Mohist.Server.Agent.Services;

/// <summary>
/// Internal seam used by <see cref="AgentConnectionStore.DeleteAsync"/>
/// to cascade-delete provider-side rows (inbox, outbox, …) for one
/// Connection. The interface is intentionally narrow — a single
/// project/connection-scoped delete — so the producer can stay free of
/// Slack provider types while still honoring the spec's "delete
/// Connection clears provider integration records" rule. Implementations
/// live in <c>Mohist.Server.Infrastructure.Slack</c>.
/// </summary>
public interface IAgentConnectionProviderCleanup
{
    Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default);
}
