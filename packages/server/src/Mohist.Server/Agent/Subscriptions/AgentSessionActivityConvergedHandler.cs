using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Orleans;

namespace Mohist.Server.Agent.Subscriptions;

[Subscription(
    Type = EventCatalog.ReverseDns.AgentSessionActivityConverged,
    Identity = "Mohist.Server.Agent.Subscriptions.AgentSessionActivityConvergedHandler")]
public sealed class AgentSessionActivityConvergedHandler(IGrainFactory grains)
    : ICloudEventHandler<AgentSessionActivityConverged>
{
    public bool Filter(CloudEvent<AgentSessionActivityConverged> evt) =>
        !string.IsNullOrWhiteSpace(evt.Data.SessionId)
        && evt.Data.SettledJobIds is { Count: > 0 }
        && evt.Data.SettledTurnIds is { Count: > 0 };

    public async Task HandleAsync(
        CloudEvent<AgentSessionActivityConverged> evt,
        CancellationToken ct)
    {
        var fact = evt.Data;
        var command = new AgentJobActivityConvergence(
            fact.SessionId,
            fact.Observation,
            fact.ContextGeneration,
            fact.BindingEpoch,
            fact.SettledTurnIds.ToArray(),
            fact.SettledJobIds.ToArray(),
            fact.SupersededOperationIds.ToArray(),
            new DateTimeOffset(
                fact.RecordedAt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(fact.RecordedAt, DateTimeKind.Utc)
                    : fact.RecordedAt.ToUniversalTime()));

        foreach (var jobId in fact.SettledJobIds.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(jobId))
                continue;
            await grains.GetGrain<IAgentJobGrain>(jobId)
                .ApplyActivityConvergenceAsync(command);
        }
    }
}
