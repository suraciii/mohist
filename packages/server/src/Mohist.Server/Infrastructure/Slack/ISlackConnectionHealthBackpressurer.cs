using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Internal seam used by <see cref="SlackOutboxStore"/> to flip a
/// Connection's <see cref="ConnectionHealthKind"/> to Degraded with a
/// Backpressured reason. Defined here — not in the Agent domain — so
/// <see cref="AgentConnectionStore"/> can implement it without taking
/// a Slack-side dependency, and so the Slack store's
/// <see cref="SlackOutboxStore"/> backpressure flow does not loop back
/// into the Agent domain through the public Connection store.
/// </summary>
public interface ISlackConnectionHealthBackpressurer
{
    Task FlipBackpressuredAsync(string projectId, string connectionId, string reason, CancellationToken ct = default);

    Task<int> RecoverBackpressuredAsync(string projectId, string connectionId, CancellationToken ct = default);
}
