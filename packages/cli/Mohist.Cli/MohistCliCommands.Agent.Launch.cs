using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static async Task RenderNeedsSetupAsync(MohistCliApi api, JsonObject envelope)
    {
        var error = envelope["error"]?.GetValue<string>()
            ?? "This Agent needs setup before it can accept new work.";
        api.Error.WriteLine($"{error} (code=agent_needs_setup)");
        if (envelope["details"] is JsonObject details
            && details["gaps"] is JsonArray gaps
            && gaps.Count > 0)
        {
            api.Error.WriteLine("gaps:");
            foreach (var gapNode in gaps.OfType<JsonObject>())
            {
                var message = gapNode["message"]?.GetValue<string>() ?? "";
                var action = gapNode["action"]?.GetValue<string>() ?? "";
                var first = !string.IsNullOrWhiteSpace(message) ? message : "(missing message)";
                var line = $"  - {first}";
                if (!string.IsNullOrWhiteSpace(action))
                    line += $" — {action}";
                api.Error.WriteLine(line);
            }
        }
        if (envelope["details"] is JsonObject setupDetails
            && setupDetails["setup"] is JsonObject setup)
        {
            var label = setup["label"]?.GetValue<string>() ?? "";
            var path = setup["path"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(path))
                api.Error.WriteLine($"Fix in: {label} ({path})");
        }
        await Task.CompletedTask;
    }

    private static object? BuildLaunchContext(int? issue, int? epic, string? repository, string? workspace)
    {
        if (issue is null && epic is null && string.IsNullOrWhiteSpace(repository) && string.IsNullOrWhiteSpace(workspace))
            return null;

        return new
        {
            issueNumber = issue,
            epicNumber = epic,
            repository = string.IsNullOrWhiteSpace(repository) ? null : repository,
            workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace,
        };
    }
}
