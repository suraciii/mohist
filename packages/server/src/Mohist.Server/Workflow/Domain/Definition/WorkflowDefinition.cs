using System.Text.Json;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Definition;

public sealed record TaskDefinition(
    string Id,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null,
    string[]? DependsOn = null)
{
    public string[]? OnSuccessEmit { get; init; }
}

public sealed record CheckFailureRetry(int Limit, TaskDefinition Task);

public sealed record CheckFailureAction(CheckFailureRetry? Retry = null);

public sealed record CheckDefinition(
    string Name,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null,
    CheckFailureAction? OnFailure = null);

public sealed record StageResetAction(
    string[]? Tasks = null,
    string[]? Checks = null,
    bool Approval = false);

public sealed record StageEventPolicy(StageResetAction Reset);

public sealed record WorkflowTasksFromDefinition(
    string Uses,
    Dictionary<string, JsonElement?>? With = null);

public sealed record StageDefinition(
    string Stage,
    List<TaskDefinition> Tasks,
    List<CheckDefinition> Checks,
    WorkflowTasksFromDefinition? TasksFrom = null,
    bool RequiresApproval = false,
    Dictionary<string, JsonElement?>? Variables = null,
    Dictionary<string, StageEventPolicy>? On = null);

public sealed record WorkflowDefinition(
    string Id,
    List<StageDefinition> Stages,
    string? Name = null,
    Dictionary<string, JsonElement?>? Variables = null,
    Dictionary<string, JsonElement?>? Defaults = null,
    Dictionary<string, string>? Artifacts = null);

public static class WorkflowDefinitionValidator
{
    public static void Validate(WorkflowDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new WorkflowDomainException("WorkflowDefinition requires an id");

        if (definition.Stages.Count == 0)
            throw new WorkflowDomainException($"WorkflowDefinition {definition.Id} requires at least one stage");

        var seenStages = new HashSet<string>();
        foreach (var stage in definition.Stages)
        {
            if (!seenStages.Add(stage.Stage))
                throw new WorkflowDomainException($"WorkflowDefinition {definition.Id} declares duplicate stage {stage.Stage}");

            var taskIds = new HashSet<string>();
            foreach (var task in stage.Tasks)
            {
                if (!taskIds.Add(task.Id))
                    throw new WorkflowDomainException($"WorkflowDefinition {definition.Id} declares duplicate task {stage.Stage}:{task.Id}");
            }

            var checkNames = new HashSet<string>();
            foreach (var check in stage.Checks)
            {
                if (!checkNames.Add(check.Name))
                    throw new WorkflowDomainException($"WorkflowDefinition {definition.Id} declares duplicate check {stage.Stage}:{check.Name}");
            }
        }
    }
}
