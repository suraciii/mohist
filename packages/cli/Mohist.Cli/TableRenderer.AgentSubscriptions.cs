using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderAgentSubscriptionList(JsonNode? data)
    {
        if (data is not JsonObject envelope)
        {
            _out.WriteLine(data?.ToJsonString() ?? "");
            return;
        }

        _out.WriteLine($"state: {StringOf(envelope, "state")}");
        _out.WriteLine($"agent status: {StringOf(envelope, "agentStatus")}");
        _out.WriteLine($"readiness: {StringOf(envelope, "readiness")}");
        _out.WriteLine($"connection: {StringOf(envelope, "connection")}");
        var rows = envelope["subscriptions"] as JsonArray;
        if (rows is null || rows.Count == 0)
        {
            _out.WriteLine("No subscriptions");
            return;
        }

        WriteTable(new[] { "position", "name", "match", "responsePrompt", "status", "continue" }, new[] { 10, 28, 42, 42, 12, 10 },
            rows.OfType<JsonObject>().Select(subscription => new[]
            {
                NumberOf(subscription, "position"),
                StringOf(subscription, "name"),
                StringOf(subscription, "match"),
                StringOf(subscription, "responsePrompt"),
                StringOf(subscription, "status"),
                BoolOf(subscription, "continue") ? "true" : "false",
            }).ToList());
    }

    private void RenderAgentSubscription(JsonNode? data)
    {
        if (data is not JsonObject subscription)
        {
            _out.WriteLine(data?.ToJsonString() ?? "");
            return;
        }

        _out.WriteLine($"{StringOf(subscription, "name")} ({StringOf(subscription, "id")})");
        _out.WriteLine($"position: {StringOf(subscription, "position")}");
        _out.WriteLine($"match: {StringOf(subscription, "match")}");
        _out.WriteLine($"responsePrompt: {StringOf(subscription, "responsePrompt")}");
        _out.WriteLine($"status: {StringOf(subscription, "status")}");
        _out.WriteLine($"continue: {BoolOf(subscription, "continue")}");
    }
}
