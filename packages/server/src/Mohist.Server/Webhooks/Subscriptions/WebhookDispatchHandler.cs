using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Webhooks.Domain;
using Mohist.Server.Webhooks.Services;

namespace Mohist.Server.Webhooks.Subscriptions;

[Subscription(
    Type = "*",
    Identity = "Mohist.Server.Events.Subscriptions.WebhookDispatchHandler")]
public sealed class WebhookDispatchHandler : ICloudEventHandler
{
    private const int ErrorSummaryMaxLength = 1024;

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

        var secretStore = services.GetRequiredService<ISecretStore>();
        var client = services.GetRequiredService<IWebhookHttpClient>();
        var renderer = services.GetRequiredService<WebhookPayloadRenderer>();
        byte[]? payload = null;

        foreach (var subscription in subscriptions)
        {
            var compileResult = EventMatchExpression.Compile(subscription.Match);
            if (!compileResult.IsSuccess || !compileResult.Expression!.Matches(new CloudEventEventMatchInput(evt)))
                continue;

            try
            {
                var secret = await secretStore.LoadAsync(
                    new SecretStoreAddress(projectId, subscription.Id, SecretKind.WebhookSecret),
                    ct);
                payload ??= renderer.Render(evt);
                await client.SendAsync(subscription.TargetUrl, payload, secret, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    ex,
                    "Webhook delivery failed for subscription {SubscriptionId} and event {EventId}",
                    subscription.Id,
                    evt.Id);
                await RecordFailureAsync(store, projectId, subscription, evt, ex, ct);
            }
        }
    }

    private async Task RecordFailureAsync(
        WebhookSubscriptionStore store,
        string projectId,
        WebhookSubscription subscription,
        CloudEvent evt,
        Exception exception,
        CancellationToken ct)
    {
        try
        {
            await store.RecordFailureAsync(new WebhookDeliveryFailure
            {
                Id = Guid.NewGuid().ToString("N"),
                ProjectId = projectId,
                SubscriptionId = subscription.Id,
                EventId = evt.Id,
                EventType = evt.Type,
                TargetUrl = subscription.TargetUrl,
                ErrorSummary = Summarize(exception),
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

    private static string Summarize(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return message.Length <= ErrorSummaryMaxLength ? message : message[..ErrorSummaryMaxLength];
    }
}
