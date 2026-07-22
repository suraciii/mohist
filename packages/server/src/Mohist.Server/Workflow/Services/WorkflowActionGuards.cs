using System.Text.Json;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

internal static class WorkflowActionGuards
{
    public static IReadOnlyList<ValidationError> Validate(WorkflowDefinition definition)
    {
        var errors = new List<ValidationError>();
        foreach (var (stage, stageIndex) in definition.Stages.Select((value, index) => (value, index)))
        {
            foreach (var (task, taskIndex) in stage.Tasks.Select((value, index) => (value, index)))
                Add(errors, task, $"stages[{stageIndex}].tasks[{taskIndex}]");
            foreach (var (check, checkIndex) in stage.Checks.Select((value, index) => (value, index)))
                Add(errors, check, $"stages[{stageIndex}].checks[{checkIndex}]");
        }

        foreach (var (task, index) in definition.Approval?.Feedback?.Tasks?.Select((value, index) => (value, index)) ?? [])
            Add(errors, task, $"approval.feedback.tasks[{index}]");

        return errors.OrderBy(error => error.Path, StringComparer.Ordinal).ToArray();
    }

    private static void Add(List<ValidationError> errors, TaskDefinition task, string path)
    {
        if (!IsInlineAgent(task.Uses) || task.With is null) return;

        if (task.With.TryGetValue("agent", out _))
            errors.Add(Error($"{path}.with.agent", $"Workflow task '{task.Id}' declares legacy agent configuration under 'with.agent'."));
        if (task.With.TryGetValue("kind", out _))
            errors.Add(Error($"{path}.with.kind", $"Workflow task '{task.Id}' declares legacy execution discriminator 'with.kind'."));
        if (task.With.TryGetValue("type", out _))
            errors.Add(Error($"{path}.with.type", $"Workflow task '{task.Id}' declares legacy execution discriminator 'with.type'."));
        if (task.With.TryGetValue("expect", out var expect) && HasCompletionPolicy(expect))
            errors.Add(Error($"{path}.with.expect", $"Workflow task '{task.Id}' declares Workflow completion policy under 'with.expect'."));
    }

    private static void Add(List<ValidationError> errors, CheckDefinition check, string path)
    {
        if (!IsInlineAgent(check.Uses) || check.With is null) return;
        if (check.With.TryGetValue("agent", out _))
            errors.Add(Error($"{path}.with.agent", $"Workflow check '{check.Id}' declares legacy agent configuration under 'with.agent'."));
        if (check.With.TryGetValue("kind", out _))
            errors.Add(Error($"{path}.with.kind", $"Workflow check '{check.Id}' declares legacy execution discriminator 'with.kind'."));
        if (check.With.TryGetValue("type", out _))
            errors.Add(Error($"{path}.with.type", $"Workflow check '{check.Id}' declares legacy execution discriminator 'with.type'."));
    }

    private static bool HasCompletionPolicy(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object }) return false;
        return value.Value.EnumerateObject().Any(property => property.Name is "files" or "markers" or "failIf");
    }

    private static bool IsInlineAgent(string uses) =>
        uses is "mohist/opencode" or "mohist/pi";

    private static ValidationError Error(string path, string message) =>
        new(path, message, ValidationSource.Action);
}
