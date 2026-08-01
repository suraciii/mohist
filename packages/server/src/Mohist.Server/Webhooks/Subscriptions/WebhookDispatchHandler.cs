using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Webhooks.Domain;
using Mohist.Server.Webhooks.Services;

namespace Mohist.Server.Webhooks.Subscriptions;

[Subscription(
    Type = "*",
    Identity = "Mohist.Server.Events.Subscriptions.WebhookDispatchHandler")]
public sealed class WebhookDispatchHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookDispatchHandler> _log;

    public WebhookDispatchHandler(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<WebhookDispatchHandler> log)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt is not null;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => DispatchAsync(evt, ct);

    private async Task DispatchAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadProjectId(evt.Extensions, out var projectId))
        {
            _log.LogDebug("Webhook dispatch skipped: event {EventType} {EventId} carries no project id", evt.Type, evt.Id);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<WebhookSubscriptionStore>();
        var subscriptions = await store.ListAsync(projectId, includeArchived: false, ct);
        if (subscriptions.Count == 0)
            return;

        var client = services.GetRequiredService<IWebhookHttpClient>();
        var renderer = services.GetRequiredService<WebhookPayloadRenderer>();
        byte[]? payload = null;

        foreach (var subscription in subscriptions)
        {
            if (!ShouldDeliver(subscription, evt))
                continue;

            try
            {
                payload ??= renderer.Render(evt);
                var auth = await store.ResolveAuthMaterialAsync(subscription, ct);
                var signingSecret = await store.LoadSigningSecretAsync(projectId, subscription.Id, ct);
                var result = await client.SendAsync(subscription.TargetUrl, payload, auth, signingSecret, ct);
                if (!result.Success)
                {
                    _log.LogWarning(
                        "Webhook delivery to {SubscriptionId} failed for event {EventId}: {Error}",
                        subscription.Id, evt.Id, result.Error);
                    await RecordFailureAsync(store, projectId, subscription, evt, result, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    ex,
                    "Webhook delivery failed for subscription {SubscriptionId} and event {EventId}",
                    subscription.Id,
                    evt.Id);
                await RecordFailureAsync(store, projectId, subscription, evt, new WebhookDeliveryResult(false, null, null, ex.Message, 0), ct);
            }
        }
    }

    private static bool ShouldDeliver(WebhookSubscription subscription, CloudEvent evt)
    {
        // Event-type selection: "selected" means only the chosen types are delivered.
        if (subscription.EventSelectionMode == WebhookEventSelectionMode.Selected
            && !subscription.EventTypes.Contains(evt.Type))
        {
            return false;
        }

        // CEL is an optional advanced filter applied in addition to the selected events.
        if (string.IsNullOrWhiteSpace(subscription.Match))
            return true;
        var compiled = EventMatchExpression.Compile(subscription.Match);
        if (!compiled.IsSuccess)
            return false;
        return compiled.Expression!.Matches(new CloudEventEventMatchInput(evt));
    }

    private async Task RecordFailureAsync(
        WebhookSubscriptionStore store,
        string projectId,
        WebhookSubscription subscription,
        CloudEvent evt,
        WebhookDeliveryResult result,
        CancellationToken ct)
    {
        try
        {
            var summary = string.IsNullOrWhiteSpace(result.Error) ? "delivery failed" : result.Error;
            await store.RecordFailureAsync(new WebhookDeliveryFailure
            {
                Id = Guid.NewGuid().ToString("N"),
                ProjectId = projectId,
                SubscriptionId = subscription.Id,
                EventId = evt.Id,
                EventType = evt.Type,
                TargetUrl = subscription.TargetUrl,
                ResponseStatus = result.StatusCode,
                DurationMs = result.DurationMs > 0 ? (int)result.DurationMs : null,
                ErrorSummary = Truncate(summary, 1024),
                OccurredAt = _timeProvider.GetUtcNow(),
            }, ct);
        }
        catch (Exception recordException) when (!ct.IsCancellationRequested)
        {
            _log.LogError(
                recordException,
                "Webhook delivery failure could not be recorded for subscription {SubscriptionId} and event {EventId}",
                subscription.Id,
                evt.Id);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
