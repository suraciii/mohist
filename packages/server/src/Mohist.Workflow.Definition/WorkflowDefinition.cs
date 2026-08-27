using System.Text.Json;

namespace Mohist.Workflow.Definition;

public sealed record ApprovalConfig(ApprovalFeedbackConfig? Feedback = null);

public sealed record ApprovalFeedbackConfig(IReadOnlyList<TaskDefinition>? Tasks = null);

public sealed record TaskArtifactDeclaration(string Path);

public sealed record TaskArtifactCapture(IReadOnlyList<TaskArtifactDeclaration> Files)
{
    public bool IsEmpty => Files is null || Files.Count == 0;
}

public sealed record TaskDefinition(
    string Id,
    string? Title = null,
    string Uses = "",
    Dictionary<string, JsonElement?>? With = null,
    Dictionary<string, JsonElement?>? Expect = null,
    TaskArtifactCapture? Artifacts = null,
    Dictionary<string, string>? SetVars = null,
    RecoveryDefinition? Recovery = null);

public sealed record RecoveryDefinition(
    int Budget,
    IReadOnlyList<RecoveryHandlerDefinition> Handlers);

public sealed record RecoveryHandlerDefinition(
    string? When,
    IReadOnlyList<TaskDefinition> Tasks,
    bool RetrySelf);

public sealed record CheckDefinition(
    string Id,
    string? Title = null,
    string Uses = "",
    Dictionary<string, JsonElement?>? With = null);

public sealed record StageDefinition(
    string Stage,
    IReadOnlyList<TaskDefinition> Tasks,
    IReadOnlyList<CheckDefinition> Checks,
    bool RequiresApproval = false,
    string? LockBehavior = null,
    IReadOnlyList<string>? Resources = null);

public sealed record WorkflowDefinition(
    IReadOnlyList<StageDefinition> Stages,
    ApprovalConfig? Approval = null,
    IReadOnlyDictionary<string, RecoveryDefinition>? Recoveries = null);

public sealed record WorkflowProfile(
    string Id,
    string Name,
    string Description,
    WorkflowDefinition Definition);

public sealed record WorkflowStructure(
    string Id,
    IReadOnlyList<StageStructure> Stages);

public sealed record StageStructure(
    string Stage,
    bool RequiresApproval);
