using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static async Task RenderExecutabilityRejectedAsync(MohistCliApi api, JsonObject envelope)
    {
        var error = envelope["error"]?.GetValue<string>()
            ?? "This Agent cannot accept new work until its execution settings are resolved.";
        var code = envelope["code"]?.GetValue<string>() ?? "agent_not_executable";
        api.Error.WriteLine($"{error} (code={code})");
        if (envelope["details"] is JsonObject details
            && details["state"] is JsonValue state
            && !string.IsNullOrWhiteSpace(state.GetValue<string>()))
            api.Error.WriteLine($"executability: {state.GetValue<string>()}");
        if (envelope["details"] is JsonObject detailsWithGaps
            && detailsWithGaps["gaps"] is JsonArray gaps
            && gaps.Count > 0)
        {
            api.Error.WriteLine("gaps:");
            foreach (var gapNode in gaps.OfType<JsonObject>())
            {
                var message = gapNode["message"]?.GetValue<string>() ?? "";
                var action = gapNode["nextAction"]?.GetValue<string>() ?? "";
                var first = !string.IsNullOrWhiteSpace(message) ? message : "(missing message)";
                var line = $"  - {first}";
                if (!string.IsNullOrWhiteSpace(action))
                    line += $" - {action}";
                api.Error.WriteLine(line);
                if (gapNode["fixEntryPoint"] is not JsonObject fix)
                    continue;
                var label = fix["label"]?.GetValue<string>() ?? "";
                var path = fix["path"]?.GetValue<string>() ?? "";
                var command = fix["command"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(path))
                    api.Error.WriteLine($"Fix in: {label} ({path})");
                if (!string.IsNullOrWhiteSpace(command))
                    api.Error.WriteLine($"Command: {command}");
            }
        }
        await Task.CompletedTask;
    }
}
