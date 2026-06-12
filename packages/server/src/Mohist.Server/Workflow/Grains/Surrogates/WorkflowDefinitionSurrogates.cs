using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Orleans.Serialization;

namespace Mohist.Server.Workflow.Grains.Surrogates;

[GenerateSerializer]
public struct WorkflowDefinitionSurrogate
{
    [Id(0)] public string Id;
    [Id(1)] public List<StageDefinition> Stages;
    [Id(2)] public string? Name;
    [Id(3)] public Dictionary<string, JsonElement?>? Variables;
    [Id(4)] public Dictionary<string, JsonElement?>? Defaults;
    [Id(5)] public Dictionary<string, string>? Artifacts;
}

[RegisterConverter]
public sealed class WorkflowDefinitionSurrogateConverter : IConverter<WorkflowDefinition, WorkflowDefinitionSurrogate>
{
    public WorkflowDefinition ConvertFromSurrogate(in WorkflowDefinitionSurrogate surrogate) =>
        new(surrogate.Id, surrogate.Stages, surrogate.Name, surrogate.Variables, surrogate.Defaults, surrogate.Artifacts);

    public WorkflowDefinitionSurrogate ConvertToSurrogate(in WorkflowDefinition value) => new()
    {
        Id = value.Id,
        Stages = value.Stages,
        Name = value.Name,
        Variables = value.Variables,
        Defaults = value.Defaults,
        Artifacts = value.Artifacts,
    };
}

[GenerateSerializer]
public struct StageDefinitionSurrogate
{
    [Id(0)] public string Stage;
    [Id(1)] public List<TaskDefinition> Tasks;
    [Id(2)] public List<CheckDefinition> Checks;
    [Id(4)] public bool RequiresApproval;
    [Id(5)] public Dictionary<string, JsonElement?>? Variables;
    [Id(7)] public string? LockBehavior;
    [Id(8)] public List<string>? Resources;
}

[RegisterConverter]
public sealed class StageDefinitionSurrogateConverter : IConverter<StageDefinition, StageDefinitionSurrogate>
{
    public StageDefinition ConvertFromSurrogate(in StageDefinitionSurrogate surrogate) =>
        new(surrogate.Stage, surrogate.Tasks, surrogate.Checks, surrogate.RequiresApproval, surrogate.Variables, surrogate.LockBehavior, surrogate.Resources);

    public StageDefinitionSurrogate ConvertToSurrogate(in StageDefinition value) => new()
    {
        Stage = value.Stage,
        Tasks = value.Tasks,
        Checks = value.Checks,
        RequiresApproval = value.RequiresApproval,
        Variables = value.Variables,
        LockBehavior = value.LockBehavior,
        Resources = value.Resources,
    };
}

[GenerateSerializer]
public struct TaskDefinitionSurrogate
{
    [Id(0)] public string Id;
    [Id(1)] public string Title;
    [Id(2)] public string? Uses;
    [Id(3)] public Dictionary<string, JsonElement?>? With;
    [Id(4)] public TaskArtifactCapture? Artifacts;
}

[RegisterConverter]
public sealed class TaskDefinitionSurrogateConverter : IConverter<TaskDefinition, TaskDefinitionSurrogate>
{
    public TaskDefinition ConvertFromSurrogate(in TaskDefinitionSurrogate surrogate) =>
        new(surrogate.Id, surrogate.Title, surrogate.Uses, surrogate.With, surrogate.Artifacts);

    public TaskDefinitionSurrogate ConvertToSurrogate(in TaskDefinition value) => new()
    {
        Id = value.Id,
        Title = value.Title,
        Uses = value.Uses,
        With = value.With,
        Artifacts = value.Artifacts,
    };
}

[GenerateSerializer]
public struct CheckDefinitionSurrogate
{
    [Id(0)] public string Name;
    [Id(1)] public string Title;
    [Id(2)] public string? Uses;
    [Id(3)] public Dictionary<string, JsonElement?>? With;
    [Id(4)] public CheckFailureAction? OnFailure;
}

[RegisterConverter]
public sealed class CheckDefinitionSurrogateConverter : IConverter<CheckDefinition, CheckDefinitionSurrogate>
{
    public CheckDefinition ConvertFromSurrogate(in CheckDefinitionSurrogate surrogate) =>
        new(surrogate.Name, surrogate.Title, surrogate.Uses, surrogate.With, surrogate.OnFailure);

    public CheckDefinitionSurrogate ConvertToSurrogate(in CheckDefinition value) => new()
    {
        Name = value.Name,
        Title = value.Title,
        Uses = value.Uses,
        With = value.With,
        OnFailure = value.OnFailure,
    };
}

[GenerateSerializer]
public struct CheckFailureActionSurrogate
{
    [Id(0)] public CheckFailureRepair? Repair;
}

[RegisterConverter]
public sealed class CheckFailureActionSurrogateConverter : IConverter<CheckFailureAction, CheckFailureActionSurrogate>
{
    public CheckFailureAction ConvertFromSurrogate(in CheckFailureActionSurrogate surrogate) =>
        new(surrogate.Repair);

    public CheckFailureActionSurrogate ConvertToSurrogate(in CheckFailureAction value) => new()
    {
        Repair = value.Repair,
    };
}

[GenerateSerializer]
public struct CheckFailureRepairSurrogate
{
    [Id(0)] public int Limit;
    [Id(1)] public TaskDefinition Task;
    [Id(2)] public TaskDefinition? VerifyTask;
}

[RegisterConverter]
public sealed class CheckFailureRepairSurrogateConverter : IConverter<CheckFailureRepair, CheckFailureRepairSurrogate>
{
    public CheckFailureRepair ConvertFromSurrogate(in CheckFailureRepairSurrogate surrogate) =>
        new(surrogate.Limit, surrogate.Task, surrogate.VerifyTask);

    public CheckFailureRepairSurrogate ConvertToSurrogate(in CheckFailureRepair value) => new()
    {
        Limit = value.Limit,
        Task = value.Task,
        VerifyTask = value.VerifyTask,
    };
}
