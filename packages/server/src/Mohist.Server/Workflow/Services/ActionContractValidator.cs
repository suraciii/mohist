using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

internal static class ActionContractValidator
{
    public const string AgentTurnCapability = "agent-turn";
    private const string EngineReservedKey = "working-directory";
    private const string OpenSpecTasksUses = "mohist/openspec-tasks";

    private static readonly string[] CanonicalKindOrder = ["string", "number", "boolean", "object", "array"];

    public static IReadOnlyList<ValidationError> Validate(WorkflowDefinition? definition, ActionCatalog catalog)
    {
        if (definition is null) return [];

        var actionsByName = new Dictionary<string, ActionCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in catalog.Actions)
        {
            actionsByName.TryAdd(entry.Name, entry);
        }
        actionsByName[VirtualActionManifests.MohistAgentUses] = VirtualActionManifests.MohistAgent;

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

        if (definition.Recoveries is not null)
        {
            foreach (var (name, recovery) in definition.Recoveries)
            {
                ValidateRecoveryTasks(errors, recovery, $"recoveries.{name}", actionsByName, tombstonesByName);
            }
        }

        return errors
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<ValidationError> ValidateAgentAction(
        WorkflowDefinition? definition,
        string agentAction,
        ActionCatalog catalog)
    {
        var errors = new List<ValidationError>();
        var selected = catalog.Actions.FirstOrDefault(action =>
            string.Equals(action.Name, agentAction, StringComparison.Ordinal));
        var tombstone = catalog.Tombstones.FirstOrDefault(action =>
            string.Equals(action.Name, agentAction, StringComparison.Ordinal));

        if (tombstone is not null)
            errors.Add(new ValidationError("agentAction", $"Agent Action '{agentAction}' was removed: {tombstone.Guidance}", ValidationSource.Action));
        else if (selected is null)
            errors.Add(new ValidationError("agentAction", $"Agent Action '{agentAction}' is not available in the current Runner catalog", ValidationSource.Action));
        else if (selected.Capabilities?.Contains(AgentTurnCapability, StringComparer.Ordinal) != true)
            errors.Add(new ValidationError("agentAction", $"Action '{agentAction}' does not declare the '{AgentTurnCapability}' capability", ValidationSource.Action));

        if (definition is not null)
        {
            foreach (var (path, uses) in EnumerateUses(definition))
            {
                var action = catalog.Actions.FirstOrDefault(entry => string.Equals(entry.Name, uses, StringComparison.Ordinal));
                if (action?.Capabilities?.Contains(AgentTurnCapability, StringComparer.Ordinal) == true
                    && !string.Equals(uses, agentAction, StringComparison.Ordinal))
                {
                    errors.Add(new ValidationError(
                        $"{path}.uses",
                        $"Agent Action binding '{agentAction}' cannot be mixed with literal Agent Action '{uses}'",
                        ValidationSource.Action));
                }
            }
        }

        return errors
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<(string Path, string Uses)> EnumerateUses(WorkflowDefinition definition)
    {
        static IEnumerable<(string Path, string Uses)> TaskUses(TaskDefinition task, string path)
        {
            yield return (path, task.Uses);
            if (TryReadOpenSpecGeneratedTask(task, out var generatedTask))
                yield return ($"{path}.with.task", generatedTask.Uses);
            if (task.Recovery?.Handlers is not { Count: > 0 } handlers) yield break;
            for (var handlerIndex = 0; handlerIndex < handlers.Count; handlerIndex++)
            {
                var tasks = handlers[handlerIndex].Tasks ?? [];
                for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
                    foreach (var item in TaskUses(tasks[taskIndex], $"{path}.recovery.handlers[{handlerIndex}].tasks[{taskIndex}]"))
                        yield return item;
            }
        }

        for (var stageIndex = 0; stageIndex < definition.Stages.Count; stageIndex++)
        {
            var stage = definition.Stages[stageIndex];
            for (var taskIndex = 0; taskIndex < stage.Tasks.Count; taskIndex++)
                foreach (var item in TaskUses(stage.Tasks[taskIndex], $"stages[{stageIndex}].tasks[{taskIndex}]"))
                    yield return item;
            for (var checkIndex = 0; checkIndex < stage.Checks.Count; checkIndex++)
                yield return ($"stages[{stageIndex}].checks[{checkIndex}]", stage.Checks[checkIndex].Uses);
        }

        var feedbackTasks = definition.Approval?.Feedback?.Tasks ?? [];
        for (var taskIndex = 0; taskIndex < feedbackTasks.Count; taskIndex++)
            foreach (var item in TaskUses(feedbackTasks[taskIndex], $"approval.feedback.tasks[{taskIndex}]"))
                yield return item;

        if (definition.Recoveries is null) yield break;
        foreach (var (name, recovery) in definition.Recoveries)
        {
            for (var handlerIndex = 0; handlerIndex < recovery.Handlers.Count; handlerIndex++)
            {
                var tasks = recovery.Handlers[handlerIndex].Tasks ?? [];
                for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
                    foreach (var item in TaskUses(tasks[taskIndex], $"recoveries.{name}.handlers[{handlerIndex}].tasks[{taskIndex}]"))
                        yield return item;
            }
        }
    }

    private static void ValidateTask(
        List<ValidationError> errors,
        TaskDefinition task,
        string taskPath,
        IReadOnlyDictionary<string, ActionCatalogEntry> actionsByName,
        IReadOnlyDictionary<string, ActionCatalogTombstone> tombstonesByName)
    {
        ValidateUses(errors, "Task", task.Id, task.Uses, taskPath, actionsByName, tombstonesByName);

        if (!string.IsNullOrEmpty(task.Uses)
            && actionsByName.TryGetValue(task.Uses, out var action))
        {
            ValidateWith(errors, task.Id, "Task", taskPath, task.With, action);

            if (string.Equals(task.Uses, OpenSpecTasksUses, StringComparison.Ordinal))
            {
                ValidateOpenSpecGeneratedTask(errors, task, taskPath, actionsByName, tombstonesByName);
            }
        }

        ValidateRecoveryTasks(errors, task.Recovery, $"{taskPath}.recovery", actionsByName, tombstonesByName);
    }

    private static void ValidateRecoveryTasks(
        List<ValidationError> errors,
        RecoveryDefinition? recovery,
        string recoveryPath,
        IReadOnlyDictionary<string, ActionCatalogEntry> actionsByName,
        IReadOnlyDictionary<string, ActionCatalogTombstone> tombstonesByName)
    {
        if (recovery?.Handlers is not { Count: > 0 } handlers) return;

        for (var handlerIndex = 0; handlerIndex < handlers.Count; handlerIndex++)
        {
            var handler = handlers[handlerIndex];
            if (handler.Tasks is null) continue;
            for (var taskIndex = 0; taskIndex < handler.Tasks.Count; taskIndex++)
            {
                ValidateTask(errors, handler.Tasks[taskIndex],
                    $"{recoveryPath}.handlers[{handlerIndex}].tasks[{taskIndex}]",
                    actionsByName, tombstonesByName);
            }
        }
    }

    private static void ValidateOpenSpecGeneratedTask(
        List<ValidationError> errors,
        TaskDefinition loaderTask,
        string loaderTaskPath,
        IReadOnlyDictionary<string, ActionCatalogEntry> actionsByName,
        IReadOnlyDictionary<string, ActionCatalogTombstone> tombstonesByName)
    {
        if (!TryReadOpenSpecGeneratedTask(loaderTask, out var generatedTask)) return;
        if (string.IsNullOrWhiteSpace(generatedTask.Uses))
        {
            errors.Add(new ValidationError(
                $"{loaderTaskPath}.with.task.uses",
                "OpenSpec generated task requires a non-empty Action 'uses' value",
                ValidationSource.Action));
            return;
        }

        ValidateTask(
            errors,
            generatedTask,
            $"{loaderTaskPath}.with.task",
            actionsByName,
            tombstonesByName);
    }

    private static bool TryReadOpenSpecGeneratedTask(
        TaskDefinition loaderTask,
        out TaskDefinition generatedTask)
    {
        generatedTask = null!;
        if (!string.Equals(loaderTask.Uses, OpenSpecTasksUses, StringComparison.Ordinal)
            || loaderTask.With is null
            || !loaderTask.With.TryGetValue("task", out var taskTemplate)
            || !taskTemplate.HasValue
            || taskTemplate.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var template = taskTemplate.Value;
        var uses = template.TryGetProperty("uses", out var usesElement)
            && usesElement.ValueKind == JsonValueKind.String
                ? usesElement.GetString() ?? string.Empty
                : string.Empty;
        Dictionary<string, JsonElement?>? with = null;
        if (template.TryGetProperty("with", out var withElement)
            && withElement.ValueKind == JsonValueKind.Object)
        {
            with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            foreach (var property in withElement.EnumerateObject())
                with[property.Name] = property.Value.Clone();
        }

        generatedTask = new TaskDefinition(loaderTask.Id, Uses: uses, With: with);
        return true;
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

        if (string.Equals(check.Uses, VirtualActionManifests.MohistAgentUses, StringComparison.Ordinal))
        {
            errors.Add(new ValidationError(checkPath,
                $"Check '{Identifier(check.Id, checkPath)}' uses Action '{check.Uses}' which is not supported for check work; use it on a task only.",
                ValidationSource.Action));
            return;
        }

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

        var isMohistAgent = string.Equals(action.Name, VirtualActionManifests.MohistAgentUses, StringComparison.Ordinal);

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

                if (TemplateTokens.Contains(value.Value))
                {
                    if (isMohistAgent && string.Equals(key, "name", StringComparison.Ordinal))
                    {
                        errors.Add(new ValidationError($"{itemPath}.with.name",
                            $"{kind} '{Identifier(itemId, itemPath)}' of Action '{action.Name}' input 'name' must be a literal string; workflow template expressions are not supported",
                            ValidationSource.Action));
                    }
                    continue;
                }

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
            "number" => value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number)
                && double.IsFinite(number),
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
