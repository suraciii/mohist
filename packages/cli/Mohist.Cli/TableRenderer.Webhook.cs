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
        _out.WriteLine($"targetUrl: {StringOf(subscription, "targetUrl")}");
        _out.WriteLine($"events: {DescribeEvents(subscription)}");
        _out.WriteLine($"auth: {StringOf(subscription, "authType")}");
        _out.WriteLine($"match (advanced): {StringOf(subscription, "match")}");
        _out.WriteLine($"hasSecret: {BoolOf(subscription, "hasSecret")}");
    }

    private static string DescribeEvents(JsonObject subscription)
    {
        var mode = StringOf(subscription, "eventSelectionMode");
        if (string.IsNullOrWhiteSpace(mode)) mode = "all";
        if (mode != "selected" || subscription["eventTypes"] is not JsonNode types)
            return mode == "all" ? "all events" : mode;
        var list = types is JsonArray arr
            ? arr.Select(t => t?.GetValue<string>() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s))
            : Enumerable.Empty<string>();
        return "selected: " + string.Join(", ", list);
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
            ["occurred at", "event type", "status", "error summary"],
            [30, 38, 8, 60],
            failures.OfType<JsonObject>().Select(failure => new[]
            {
                Truncate(StringOf(failure, "occurredAt"), 30),
                Truncate(StringOf(failure, "eventType"), 38),
                StringOf(failure, "responseStatus"),
                Truncate(StringOf(failure, "errorSummary"), 60),
            }).ToList());
    }
}
