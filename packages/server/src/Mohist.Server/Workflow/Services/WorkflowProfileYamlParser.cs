using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

internal static class WorkflowProfileYamlParser
{
    public static WorkflowProfile Parse(
        string yaml,
        string fallbackId,
        ActionCatalog? catalog = null)
    {
        var result = WorkflowProfileParser.Parse(yaml, fallbackId);
        var errors = result.Errors.ToList();
        if (result.Profile is not null)
        {
            errors.AddRange(RejectRuntimeActions(result.Profile.Definition));
            if (catalog is not null)
                errors.AddRange(ActionContractValidator.Validate(result.Profile.Definition, catalog));
        }
        if (errors.Count > 0)
            throw new WorkflowDefinitionValidationException(errors
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToArray());
        return result.Profile!;
    }

    private static IReadOnlyList<ValidationError> RejectRuntimeActions(WorkflowDefinition definition)
    {
        var errors = new List<ValidationError>();
        void Visit(TaskDefinition task, string path)
        {
            if (task.Uses is "mohist/opencode" or "mohist/pi")
                errors.Add(Removed(path, task.Uses));
            if (task.With is not null
                && string.Equals(task.Uses, "mohist/task-list", StringComparison.Ordinal)
                && task.With.TryGetValue("task", out var nested)
                && nested.HasValue
                && nested.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                && nested.Value.TryGetProperty("uses", out var nestedUses)
                && nestedUses.ValueKind == System.Text.Json.JsonValueKind.String
                && nestedUses.GetString() is "mohist/opencode" or "mohist/pi")
                errors.Add(Removed($"{path}.with.task", nestedUses.GetString()!));
            if (task.Recovery?.Handlers is not { Count: > 0 } handlers) return;
            for (var h = 0; h < handlers.Count; h++)
                for (var t = 0; t < handlers[h].Tasks.Count; t++)
                    Visit(handlers[h].Tasks[t], $"{path}.recovery.handlers[{h}].tasks[{t}]");
        }
        for (var s = 0; s < definition.Stages.Count; s++)
            for (var t = 0; t < definition.Stages[s].Tasks.Count; t++)
                Visit(definition.Stages[s].Tasks[t], $"stages[{s}].tasks[{t}]");
        var feedback = definition.Approval?.Feedback?.Tasks ?? [];
        for (var t = 0; t < feedback.Count; t++) Visit(feedback[t], $"approval.feedback.tasks[{t}]");
        if (definition.Recoveries is not null)
            foreach (var (name, recovery) in definition.Recoveries)
                for (var h = 0; h < recovery.Handlers.Count; h++)
                    for (var t = 0; t < recovery.Handlers[h].Tasks.Count; t++)
                        Visit(recovery.Handlers[h].Tasks[t], $"recoveries.{name}.handlers[{h}].tasks[{t}]");
        return errors;
    }

    private static ValidationError Removed(string path, string uses) => new(
        path,
        $"Workflow Agent Action '{uses}' was removed; use 'mohist/agent' with a named Agent.",
        ValidationSource.Action);
}
