using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Orleans.Serialization;

namespace Mohist.Server.Workflow.Services;

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
    [Id(6)] public Dictionary<string, StageEventPolicy>? On;
    [Id(7)] public string? LockBehavior;
    [Id(8)] public List<string>? Resources;
}

[RegisterConverter]
public sealed class StageDefinitionSurrogateConverter : IConverter<StageDefinition, StageDefinitionSurrogate>
{
    public StageDefinition ConvertFromSurrogate(in StageDefinitionSurrogate surrogate) =>
        new(surrogate.Stage, surrogate.Tasks, surrogate.Checks, surrogate.RequiresApproval, surrogate.Variables, surrogate.On, surrogate.LockBehavior, surrogate.Resources);

    public StageDefinitionSurrogate ConvertToSurrogate(in StageDefinition value) => new()
    {
        Stage = value.Stage,
        Tasks = value.Tasks,
        Checks = value.Checks,
        RequiresApproval = value.RequiresApproval,
        Variables = value.Variables,
        On = value.On,
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
    [Id(4)] public string[]? DependsOn;
    [Id(5)] public string[]? OnSuccessEmit;
}

[RegisterConverter]
public sealed class TaskDefinitionSurrogateConverter : IConverter<TaskDefinition, TaskDefinitionSurrogate>
{
    public TaskDefinition ConvertFromSurrogate(in TaskDefinitionSurrogate surrogate) =>
        new(surrogate.Id, surrogate.Title, surrogate.Uses, surrogate.With, surrogate.DependsOn)
        {
            OnSuccessEmit = surrogate.OnSuccessEmit,
        };

    public TaskDefinitionSurrogate ConvertToSurrogate(in TaskDefinition value) => new()
    {
        Id = value.Id,
        Title = value.Title,
        Uses = value.Uses,
        With = value.With,
        DependsOn = value.DependsOn,
        OnSuccessEmit = value.OnSuccessEmit,
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

[GenerateSerializer]
public struct StageEventPolicySurrogate
{
    [Id(0)] public StageResetAction Reset;
}

[RegisterConverter]
public sealed class StageEventPolicySurrogateConverter : IConverter<StageEventPolicy, StageEventPolicySurrogate>
{
    public StageEventPolicy ConvertFromSurrogate(in StageEventPolicySurrogate surrogate) =>
        new(surrogate.Reset);

    public StageEventPolicySurrogate ConvertToSurrogate(in StageEventPolicy value) => new()
    {
        Reset = value.Reset,
    };
}

[GenerateSerializer]
public struct StageResetActionSurrogate
{
    [Id(0)] public string[]? Tasks;
    [Id(1)] public string[]? Checks;
    [Id(2)] public bool Approval;
}

[RegisterConverter]
public sealed class StageResetActionSurrogateConverter : IConverter<StageResetAction, StageResetActionSurrogate>
{
    public StageResetAction ConvertFromSurrogate(in StageResetActionSurrogate surrogate) =>
        new(surrogate.Tasks, surrogate.Checks, surrogate.Approval);

    public StageResetActionSurrogate ConvertToSurrogate(in StageResetAction value) => new()
    {
        Tasks = value.Tasks,
        Checks = value.Checks,
        Approval = value.Approval,
    };
}
