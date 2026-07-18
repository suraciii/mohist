using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderRoutingRuleList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0) { _out.WriteLine("No routing rules"); return; }
        WriteTable(new[] { "position", "name", "agent", "status", "continue" }, new[] { 10, 28, 28, 12, 10 },
            rows.OfType<JsonObject>().Select(rule => new[]
            {
                NumberOf(rule, "position"), StringOf(rule, "name"), AgentOf(rule),
                StringOf(rule, "status"), BoolOf(rule, "continue") ? "true" : "false",
            }).ToList());
    }

    private void RenderRoutingRule(JsonNode? data)
    {
        if (data is not JsonObject rule) { _out.WriteLine(data?.ToJsonString() ?? ""); return; }
        _out.WriteLine($"{StringOf(rule, "name")} ({StringOf(rule, "id")})");
        _out.WriteLine($"position: {StringOf(rule, "position")}");
        _out.WriteLine($"match: {StringOf(rule, "match")}");
        _out.WriteLine($"agent: {AgentOf(rule)}");
        _out.WriteLine($"status: {StringOf(rule, "status")}");
        _out.WriteLine($"continue: {BoolOf(rule, "continue")}");
    }

    private static string AgentOf(JsonNode rule) =>
        string.IsNullOrWhiteSpace(StringOf(rule, "agentName")) ? StringOf(rule, "agentId") : StringOf(rule, "agentName");
}
