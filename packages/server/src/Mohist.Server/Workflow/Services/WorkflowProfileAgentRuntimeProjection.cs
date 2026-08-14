using System.Text.Json;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

internal static class WorkflowProfileAgentRuntimeProjection
{
    private const string OpenCodeUses = "mohist/opencode";
    private const string PiUses = "mohist/pi";
    private const string OpenSpecTasksUses = "mohist/openspec-tasks";

    public static string? Project(string? agentAction) => agentAction switch
    {
        OpenCodeUses => "opencode",
        PiUses => "pi",
        _ => null,
    };

    public static string? Project(WorkflowDefinition? definition)
    {
        if (definition is null) return null;

        var runtimes = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = false;

        void ScanUses(string? uses)
        {
            if (string.IsNullOrWhiteSpace(uses)) return;
            if (IsDynamic(uses))
            {
                unresolved = true;
                return;
            }

            switch (uses)
            {
                case OpenCodeUses:
                    runtimes.Add("opencode");
                    break;
                case PiUses:
                    runtimes.Add("pi");
                    break;
            }
        }

        void ScanRecovery(RecoveryDefinition? recovery)
        {
            if (recovery?.Handlers is null) return;
            foreach (var handler in recovery.Handlers)
            {
                foreach (var task in handler.Tasks ?? [])
                    ScanTask(task);
            }
        }

        void ScanTask(TaskDefinition task)
        {
            if (string.Equals(task.Uses, OpenSpecTasksUses, StringComparison.Ordinal))
            {
                if (!TryGetOpenSpecTasksUses(task.With, out var nestedUses, out var nestedUnresolved))
                    unresolved = true;
                else if (nestedUnresolved)
                    unresolved = true;
                else
                    ScanUses(nestedUses);
            }
            else
                ScanUses(task.Uses);

            ScanRecovery(task.Recovery);
        }

        foreach (var stage in definition.Stages ?? [])
        {
            foreach (var task in stage.Tasks ?? [])
                ScanTask(task);

            foreach (var check in stage.Checks ?? [])
                ScanUses(check.Uses);
        }

        foreach (var task in definition.Approval?.Feedback?.Tasks ?? [])
            ScanTask(task);

        if (definition.Recoveries is not null)
        {
            foreach (var recovery in definition.Recoveries.Values)
                ScanRecovery(recovery);
        }

        return !unresolved && runtimes.Count == 1 ? runtimes.Single() : null;
    }

    private static bool TryGetOpenSpecTasksUses(
        Dictionary<string, JsonElement?>? with,
        out string? uses,
        out bool unresolved)
    {
        uses = null;
        unresolved = false;
        if (with is null || !with.TryGetValue("task", out var taskValue))
        {
            return false;
        }

        if (taskValue is null || taskValue.Value.ValueKind != JsonValueKind.Object)
        {
            unresolved = true;
            return true;
        }

        if (!taskValue.Value.TryGetProperty("uses", out var nestedUses))
        {
            return false;
        }

        if (nestedUses.ValueKind != JsonValueKind.String)
        {
            unresolved = true;
            return true;
        }

        uses = nestedUses.GetString();
        return true;
    }

    private static bool IsDynamic(string value) =>
        TemplateTokens.Contains(JsonSerializer.SerializeToElement(value));
}
