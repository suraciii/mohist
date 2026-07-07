using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Domain-to-product projection for <see cref="AgentSubscription"/>. Stored
/// as ISO-8601 strings on the wire; <c>JsonSerializer</c> parses them back
/// to <see cref="DateTimeOffset"/> on the consumer side.
/// </summary>
public static class AgentSubscriptionQuerier
{
    public static AgentSubscriptionDto ToDto(AgentSubscription subscription) => new(
        subscription.Id,
        subscription.ProjectId,
        subscription.AgentId,
        subscription.Name,
        new AgentSubscriptionFilterDto(
            subscription.Filter.Type,
            subscription.Filter.Source,
            subscription.Filter.Subject),
        subscription.ResponsePrompt,
        subscription.Priority,
        subscription.Status,
        subscription.CreatedAt.ToString("o"),
        subscription.UpdatedAt.ToString("o"));
}
