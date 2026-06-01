using System.Text.Json;

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

public sealed record CheckFailureRepair(int Limit, TaskDefinition Task);

public sealed record CheckFailureAction(CheckFailureRepair? Repair = null);

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

public sealed record StageDefinition(
    string Stage,
    List<TaskDefinition> Tasks,
    List<CheckDefinition> Checks,
    bool RequiresApproval = false,
    Dictionary<string, JsonElement?>? Variables = null,
    Dictionary<string, StageEventPolicy>? On = null,
    string? LockBehavior = null,
    List<string>? Resources = null);

public sealed record WorkflowDefinition(
    string Id,
    List<StageDefinition> Stages,
    string? Name = null,
    Dictionary<string, JsonElement?>? Variables = null,
    Dictionary<string, JsonElement?>? Defaults = null,
    Dictionary<string, string>? Artifacts = null);
