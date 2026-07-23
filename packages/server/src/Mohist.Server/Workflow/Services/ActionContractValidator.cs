using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

internal static class ActionContractValidator
{
    private const string EngineReservedKey = "working-directory";

    private static readonly string[] CanonicalKindOrder = ["string", "number", "boolean", "object", "array"];

    public static IReadOnlyList<ValidationError> Validate(WorkflowDefinition? definition, ActionCatalog catalog)
    {
        if (definition is null) return [];

        var actionsByName = new Dictionary<string, ActionCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in catalog.Actions)
        {
            actionsByName.TryAdd(entry.Name, entry);
        }

        var tombstonesByName = new Dictionary<string, ActionCatalogTombstone>(StringComparer.Ordinal);
        foreach (var entry in catalog.Tombstones)
        {
            tombstonesByName.TryAdd(entry.Name, entry);
        }

        var errors = new List<ValidationError>();

        if (definition.Stages is not null)
        {
            for (var stageIndex = 0; stageIndex < definition.Stages.Count; stageIndex++)
            {
                var stage = definition.Stages[stageIndex];
                var stagePath = $"stages[{stageIndex}]";

                if (stage.Tasks is not null)
                {
                    for (var taskIndex = 0; taskIndex < stage.Tasks.Count; taskIndex++)
                    {
                        ValidateTask(errors, stage.Tasks[taskIndex],
                            $"{stagePath}.tasks[{taskIndex}]",
                            actionsByName, tombstonesByName);
                    }
                }

                if (stage.Checks is not null)
                {
                    for (var checkIndex = 0; checkIndex < stage.Checks.Count; checkIndex++)
                    {
                        ValidateCheck(errors, stage.Checks[checkIndex],
                            $"{stagePath}.checks[{checkIndex}]",
                            actionsByName, tombstonesByName);
                    }
                }
            }
        }

        if (definition.Approval?.Feedback is { Tasks: { Count: > 0 } feedbackTasks })
        {
            for (var taskIndex = 0; taskIndex < feedbackTasks.Count; taskIndex++)
            {
                ValidateTask(errors, feedbackTasks[taskIndex],
                    $"approval.feedback.tasks[{taskIndex}]",
                    actionsByName, tombstonesByName);
            }
        }

        return errors
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateTask(
        List<ValidationError> errors,
        TaskDefinition task,
        string taskPath,
        IReadOnlyDictionary<string, ActionCatalogEntry> actionsByName,
        IReadOnlyDictionary<string, ActionCatalogTombstone> tombstonesByName)
    {
        ValidateUses(errors, "Task", task.Id, task.Uses, taskPath, actionsByName, tombstonesByName);

        if (string.IsNullOrEmpty(task.Uses)) return;
        if (!actionsByName.TryGetValue(task.Uses, out var action)) return;

        ValidateWith(errors, task.Id, "Task", taskPath, task.With, action);

        if (task.Recovery?.Handlers is { Count: > 0 } handlers)
        {
            for (var handlerIndex = 0; handlerIndex < handlers.Count; handlerIndex++)
            {
                var handler = handlers[handlerIndex];
                if (handler.Tasks is null) continue;
                for (var innerTaskIndex = 0; innerTaskIndex < handler.Tasks.Count; innerTaskIndex++)
                {
                    ValidateTask(errors, handler.Tasks[innerTaskIndex],
                        $"{taskPath}.recovery.handlers[{handlerIndex}].tasks[{innerTaskIndex}]",
                        actionsByName, tombstonesByName);
                }
            }
        }
    }

    private static void ValidateCheck(
        List<ValidationError> errors,
        CheckDefinition check,
        string checkPath,
        IReadOnlyDictionary<string, ActionCatalogEntry> actionsByName,
        IReadOnlyDictionary<string, ActionCatalogTombstone> tombstonesByName)
    {
        ValidateUses(errors, "Check", check.Id, check.Uses, checkPath, actionsByName, tombstonesByName);

        if (string.IsNullOrEmpty(check.Uses)) return;
        if (!actionsByName.TryGetValue(check.Uses, out var action)) return;

        ValidateWith(errors, check.Id, "Check", checkPath, check.With, action);
    }

    private static void ValidateUses(
        List<ValidationError> errors,
        string kind,
        string? itemId,
        string uses,
        string itemPath,
        IReadOnlyDictionary<string, ActionCatalogEntry> actionsByName,
        IReadOnlyDictionary<string, ActionCatalogTombstone> tombstonesByName)
    {
        if (string.IsNullOrEmpty(uses)) return;

        if (tombstonesByName.TryGetValue(uses, out var tombstone))
        {
            errors.Add(new ValidationError(itemPath,
                $"{kind} '{Identifier(itemId, itemPath)}' uses removed Action '{uses}': {tombstone.Guidance}",
                ValidationSource.Action));
            return;
        }

        if (!actionsByName.ContainsKey(uses))
        {
            errors.Add(new ValidationError(itemPath,
                $"{kind} '{Identifier(itemId, itemPath)}' uses unknown Action '{uses}'",
                ValidationSource.Action));
        }
    }

    private static void ValidateWith(
        List<ValidationError> errors,
        string? itemId,
        string kind,
        string itemPath,
        Dictionary<string, JsonElement?>? with,
        ActionCatalogEntry action)
    {
        var declaredInputs = new Dictionary<string, ActionCatalogInput>(StringComparer.Ordinal);
        foreach (var input in action.Inputs)
        {
            declaredInputs.TryAdd(input.Name, input);
        }

        if (with is not null)
        {
            foreach (var (key, value) in with)
            {
                if (string.Equals(key, EngineReservedKey, StringComparison.Ordinal)) continue;

                if (!declaredInputs.TryGetValue(key, out var input))
                {
                    errors.Add(new ValidationError($"{itemPath}.with.{key}",
                        $"{kind} '{Identifier(itemId, itemPath)}' of Action '{action.Name}' declares unknown input '{key}'",
                        ValidationSource.Action));
                    continue;
                }

                if (!value.HasValue) continue;

                if (TemplateTokens.Contains(value.Value)) continue;

                if (value.Value.ValueKind == JsonValueKind.Null)
                {
                    if (input.Required)
                    {
                        errors.Add(new ValidationError($"{itemPath}.with.{key}",
                            $"{kind} '{Identifier(itemId, itemPath)}' of Action '{action.Name}' input '{key}' must be {FormatKinds(input.Types)}, received null",
                            ValidationSource.Action));
                    }
                    continue;
                }

                if (!MatchesAnyKind(input.Types, value.Value))
                {
                    errors.Add(new ValidationError($"{itemPath}.with.{key}",
                        $"{kind} '{Identifier(itemId, itemPath)}' of Action '{action.Name}' input '{key}' must be {FormatKinds(input.Types)}, received {ActualKindLabel(value.Value)}",
                        ValidationSource.Action));
                }
            }
        }

        foreach (var input in action.Inputs)
        {
            if (input.Required && (with is null || !with.ContainsKey(input.Name)))
            {
                errors.Add(new ValidationError($"{itemPath}.with.{input.Name}",
                    $"{kind} '{Identifier(itemId, itemPath)}' of Action '{action.Name}' is missing required input '{input.Name}'",
                    ValidationSource.Action));
            }
        }
    }

    private static bool MatchesAnyKind(IReadOnlyList<string> declared, JsonElement value)
    {
        foreach (var kind in declared)
        {
            if (MatchesKind(kind, value)) return true;
        }
        return false;
    }

    private static bool MatchesKind(string kind, JsonElement value)
    {
        return kind switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            _ => false,
        };
    }

    private static string FormatKinds(IReadOnlyList<string> declared)
    {
        if (declared.Count == 0) return "unspecified";

        var ordered = new List<string>(declared.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in CanonicalKindOrder)
        {
            foreach (var candidate in declared)
            {
                if (string.Equals(candidate, kind, StringComparison.Ordinal) && seen.Add(candidate))
                {
                    ordered.Add(candidate);
                }
            }
        }
        foreach (var candidate in declared)
        {
            if (seen.Add(candidate)) ordered.Add(candidate);
        }
        return ordered.Count == 1 ? ordered[0] : string.Join(" or ", ordered);
    }

    private static string ActualKindLabel(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };

    private static string Identifier(string? itemId, string fallbackPath) =>
        string.IsNullOrEmpty(itemId) ? fallbackPath : itemId;
}
