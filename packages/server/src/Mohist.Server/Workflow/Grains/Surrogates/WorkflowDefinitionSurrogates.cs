using System.Text.Json;
using Mohist.Workflow.Definition;
using Orleans.Serialization;

namespace Mohist.Server.Workflow.Grains.Surrogates;

// Only runtime slices that cross grain calls need Orleans surrogates here.
// Full workflow definitions stay in the profile/persistence path and use JSON.

[GenerateSerializer]
public struct TaskArtifactDeclarationSurrogate
{
    [Id(0)] public string Path;
}

[RegisterConverter]
public sealed class TaskArtifactDeclarationSurrogateConverter : IConverter<TaskArtifactDeclaration, TaskArtifactDeclarationSurrogate>
{
    public TaskArtifactDeclaration ConvertFromSurrogate(in TaskArtifactDeclarationSurrogate surrogate) =>
        new(surrogate.Path);

    public TaskArtifactDeclarationSurrogate ConvertToSurrogate(in TaskArtifactDeclaration value) => new()
    {
        Path = value.Path,
    };
}

[GenerateSerializer]
public struct TaskArtifactCaptureSurrogate
{
    [Id(0)] public List<TaskArtifactDeclaration> Files;
}

[RegisterConverter]
public sealed class TaskArtifactCaptureSurrogateConverter : IConverter<TaskArtifactCapture, TaskArtifactCaptureSurrogate>
{
    public TaskArtifactCapture ConvertFromSurrogate(in TaskArtifactCaptureSurrogate surrogate) =>
        new(surrogate.Files);

    public TaskArtifactCaptureSurrogate ConvertToSurrogate(in TaskArtifactCapture value) => new()
    {
        Files = value.Files.ToList(),
    };
}

[GenerateSerializer]
public struct StageDefinitionSurrogate
{
    [Id(0)] public string Stage;
    [Id(1)] public List<TaskDefinition> Tasks;
    [Id(2)] public List<CheckDefinition> Checks;
    [Id(4)] public bool RequiresApproval;
    [Id(7)] public string? LockBehavior;
    [Id(8)] public List<string>? Resources;
}

[RegisterConverter]
public sealed class StageDefinitionSurrogateConverter : IConverter<StageDefinition, StageDefinitionSurrogate>
{
    public StageDefinition ConvertFromSurrogate(in StageDefinitionSurrogate surrogate) =>
        new(surrogate.Stage, surrogate.Tasks, surrogate.Checks, surrogate.RequiresApproval, surrogate.LockBehavior, surrogate.Resources);

    public StageDefinitionSurrogate ConvertToSurrogate(in StageDefinition value) => new()
    {
        Stage = value.Stage,
        Tasks = value.Tasks.ToList(),
        Checks = value.Checks.ToList(),
        RequiresApproval = value.RequiresApproval,
        LockBehavior = value.LockBehavior,
        Resources = value.Resources?.ToList(),
    };
}

[GenerateSerializer]
public struct TaskDefinitionSurrogate
{
    [Id(0)] public string Id;
    [Id(1)] public string? Title;
    [Id(2)] public string Uses;
    [Id(3)] public Dictionary<string, JsonElement?>? With;
    [Id(4)] public TaskArtifactCapture? Artifacts;
    [Id(5)] public Dictionary<string, JsonElement?>? Expect;
    [Id(6)] public Dictionary<string, string>? SetVars;
    [Id(7)] public RecoveryDefinition? Recovery;
}

[RegisterConverter]
public sealed class TaskDefinitionSurrogateConverter : IConverter<TaskDefinition, TaskDefinitionSurrogate>
{
    public TaskDefinition ConvertFromSurrogate(in TaskDefinitionSurrogate surrogate) =>
        new(surrogate.Id, surrogate.Title, surrogate.Uses, surrogate.With, surrogate.Expect, surrogate.Artifacts, surrogate.SetVars, surrogate.Recovery);

    public TaskDefinitionSurrogate ConvertToSurrogate(in TaskDefinition value) => new()
    {
        Id = value.Id,
        Title = value.Title,
        Uses = value.Uses,
        With = value.With,
        Expect = value.Expect,
        Artifacts = value.Artifacts,
        SetVars = value.SetVars,
        Recovery = value.Recovery,
    };
}

[GenerateSerializer]
public struct RecoveryDefinitionSurrogate
{
    [Id(0)] public int Budget;
    [Id(1)] public List<RecoveryHandlerDefinition> Handlers;
}

[RegisterConverter]
public sealed class RecoveryDefinitionSurrogateConverter : IConverter<RecoveryDefinition, RecoveryDefinitionSurrogate>
{
    public RecoveryDefinition ConvertFromSurrogate(in RecoveryDefinitionSurrogate surrogate) =>
        new(surrogate.Budget, surrogate.Handlers);

    public RecoveryDefinitionSurrogate ConvertToSurrogate(in RecoveryDefinition value) => new()
    {
        Budget = value.Budget,
        Handlers = value.Handlers.ToList(),
    };
}

[GenerateSerializer]
public struct RecoveryHandlerDefinitionSurrogate
{
    [Id(0)] public string? When;
    [Id(1)] public List<TaskDefinition> Tasks;
    [Id(2)] public bool RetrySelf;
}

[RegisterConverter]
public sealed class RecoveryHandlerDefinitionSurrogateConverter : IConverter<RecoveryHandlerDefinition, RecoveryHandlerDefinitionSurrogate>
{
    public RecoveryHandlerDefinition ConvertFromSurrogate(in RecoveryHandlerDefinitionSurrogate surrogate) =>
        new(surrogate.When, surrogate.Tasks, surrogate.RetrySelf);

    public RecoveryHandlerDefinitionSurrogate ConvertToSurrogate(in RecoveryHandlerDefinition value) => new()
    {
        When = value.When,
        Tasks = value.Tasks.ToList(),
        RetrySelf = value.RetrySelf,
    };
}

[GenerateSerializer]
public struct CheckDefinitionSurrogate
{
    [Id(0)] public string Id;
    [Id(1)] public string? Title;
    [Id(2)] public string Uses;
    [Id(3)] public Dictionary<string, JsonElement?>? With;
}

[RegisterConverter]
public sealed class CheckDefinitionSurrogateConverter : IConverter<CheckDefinition, CheckDefinitionSurrogate>
{
    public CheckDefinition ConvertFromSurrogate(in CheckDefinitionSurrogate surrogate) =>
        new(surrogate.Id, surrogate.Title, surrogate.Uses, surrogate.With);

    public CheckDefinitionSurrogate ConvertToSurrogate(in CheckDefinition value) => new()
    {
        Id = value.Id,
        Title = value.Title,
        Uses = value.Uses,
        With = value.With,
    };
}
