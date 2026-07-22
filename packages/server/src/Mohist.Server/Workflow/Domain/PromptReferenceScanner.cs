using System.Text.Json;
using System.Text.RegularExpressions;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Domain;

public static class PromptReferenceScanner
{
    private static readonly Regex PromptReference = new(
        @"\$\{\{\s*prompts\.([A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*)\s*\}\}",
        RegexOptions.Compiled);

    public static HashSet<string> Scan(WorkflowDefinition definition)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (definition is null) return keys;

        foreach (var stage in definition.Stages)
        {
            foreach (var task in stage.Tasks)
                ScanTask(task, keys);

            foreach (var check in stage.Checks)
            {
                ScanWith(check.With, keys);
            }
        }
        return keys;
    }

    private static void ScanTask(TaskDefinition task, HashSet<string> keys)
    {
        ScanWith(task.With, keys);
        if (task.Recovery is null) return;

        foreach (var handler in task.Recovery.Handlers)
        {
            foreach (var recoveryTask in handler.Tasks)
                ScanTask(recoveryTask, keys);
        }
    }

    private static void ScanWith(Dictionary<string, JsonElement?>? with, HashSet<string> keys)
    {
        if (with is null) return;
        foreach (var (_, value) in with)
        {
            if (!value.HasValue) continue;
            var text = value.Value.ValueKind == JsonValueKind.String
                ? value.Value.GetString() ?? value.Value.GetRawText()
                : value.Value.GetRawText();
            foreach (Match match in PromptReference.Matches(text))
            {
                if (match.Groups.Count > 1 && match.Groups[1].Success)
                    keys.Add(match.Groups[1].Value);
            }
        }
    }
}
