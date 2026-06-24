using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Orleans.Serialization;

namespace Mohist.Server.Workflow.Grains.Surrogates;

// NOTE: WorkflowDefinition (and ApprovalConfig / ApprovalFeedbackConfig /
// FeedbackTaskConfig) are intentionally NOT registered here. The control-plane
// grain no longer holds or returns WorkflowDefinition — it only goes through
// the narrow profileManager APIs (LoadStageSpecsAsync / LoadStructureAsync /
// LoadApprovalConfigAsync), which deserialize into the runtime types and hand
// the grain stage / structure / approval slices. WorkflowDefinition itself is
// an internal persistence/management concern (IssueWorkflowProfileManager,
// ProjectWorkflowProfileManager, WorkflowYamlSerializer, ResolvedTemplate)
// serialized via System.Text.Json, not Orleans. The per-stage surrogates
// below remain because StageDefinition / TaskDefinition / CheckDefinition are
// still passed into grain methods (InitializeStage, AddTasksAsync,
// BindArtifactUploadsAsync).

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
    [Id(6)] public Dictionary<string, string>? SetVars;
}

[RegisterConverter]
public sealed class TaskDefinitionSurrogateConverter : IConverter<TaskDefinition, TaskDefinitionSurrogate>
{
    public TaskDefinition ConvertFromSurrogate(in TaskDefinitionSurrogate surrogate) =>
        new(surrogate.Id, surrogate.Title, surrogate.Uses, surrogate.With, surrogate.Artifacts, surrogate.SetVars);

    public TaskDefinitionSurrogate ConvertToSurrogate(in TaskDefinition value) => new()
    {
        Id = value.Id,
        Title = value.Title,
        Uses = value.Uses,
        With = value.With,
        Artifacts = value.Artifacts,
        SetVars = value.SetVars,
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
}

[RegisterConverter]
public sealed class CheckFailureRepairSurrogateConverter : IConverter<CheckFailureRepair, CheckFailureRepairSurrogate>
{
    public CheckFailureRepair ConvertFromSurrogate(in CheckFailureRepairSurrogate surrogate) =>
        new(surrogate.Limit, surrogate.Task);

    public CheckFailureRepairSurrogate ConvertToSurrogate(in CheckFailureRepair value) => new()
    {
        Limit = value.Limit,
        Task = value.Task,
    };
}
