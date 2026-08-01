using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderWebhookSubscriptionList(JsonNode? data)
    {
        var subscriptions = AsArray(data);
        if (subscriptions.Count == 0)
        {
            _out.WriteLine("No webhook subscriptions");
            return;
        }

        WriteTable(
            ["name", "status", "target url", "has secret", "id"],
            [28, 12, 48, 12, IdSoftCap],
            subscriptions.OfType<JsonObject>().Select(subscription => new[]
            {
                Truncate(StringOf(subscription, "name"), 28),
                Truncate(StringOf(subscription, "status"), 12),
                Truncate(StringOf(subscription, "targetUrl"), 48),
                BoolOf(subscription, "hasSecret") ? "true" : "false",
                Truncate(StringOf(subscription, "id"), IdSoftCap),
            }).ToList());
    }

    private void RenderWebhookSubscription(JsonNode? data)
    {
        if (data is not JsonObject subscription)
        {
            _out.WriteLine(data?.ToJsonString() ?? "");
            return;
        }

        _out.WriteLine($"{StringOf(subscription, "name")} ({StringOf(subscription, "id")})");
        _out.WriteLine($"status: {StringOf(subscription, "status")}");
        _out.WriteLine($"match: {StringOf(subscription, "match")}");
        _out.WriteLine($"targetUrl: {StringOf(subscription, "targetUrl")}");
        _out.WriteLine($"hasSecret: {BoolOf(subscription, "hasSecret")}");
    }

    private void RenderWebhookDeliveryFailureList(JsonNode? data)
    {
        var failures = AsArray(data);
        if (failures.Count == 0)
        {
            _out.WriteLine("No webhook delivery failures");
            return;
        }

        WriteTable(
            ["occurred at", "event type", "error summary"],
            [30, 42, 60],
            failures.OfType<JsonObject>().Select(failure => new[]
            {
                Truncate(StringOf(failure, "occurredAt"), 30),
                Truncate(StringOf(failure, "eventType"), 42),
                Truncate(StringOf(failure, "errorSummary"), 60),
            }).ToList());
    }
}
