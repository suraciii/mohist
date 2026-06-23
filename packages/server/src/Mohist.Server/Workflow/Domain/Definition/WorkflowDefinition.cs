using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Definition;

public sealed record TaskArtifactDeclaration(string Path);

public sealed record TaskArtifactCapture(List<TaskArtifactDeclaration> Files)
{
    public bool IsEmpty => Files is null || Files.Count == 0;
}

public sealed record TaskOutputDefinition(string Name, string From);

public sealed record TaskDefinition(
    string Id,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null,
    TaskArtifactCapture? Artifacts = null,
    List<TaskOutputDefinition>? Outputs = null,
    Dictionary<string, string>? SetVars = null);

public sealed record CheckFailureRepair(int Limit, TaskDefinition Task, TaskDefinition? VerifyTask = null);

public sealed record CheckFailureAction(CheckFailureRepair? Repair = null);

public sealed record CheckDefinition(
    string Name,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null,
    CheckFailureAction? OnFailure = null);

public sealed record StageDefinition(
    string Stage,
    List<TaskDefinition> Tasks,
    List<CheckDefinition> Checks,
    bool RequiresApproval = false,
    Dictionary<string, JsonElement?>? Variables = null,
    string? LockBehavior = null,
    List<string>? Resources = null);

public sealed record FeedbackTaskConfig(
    string Id,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);

public sealed record ApprovalFeedbackConfig(FeedbackTaskConfig? Task = null);

public sealed record ApprovalConfig(ApprovalFeedbackConfig? Feedback = null);

public sealed record WorkflowDefinition(
    string Id,
    List<StageDefinition> Stages,
    string? Name = null,
    string? Description = null,
    Dictionary<string, JsonElement?>? Variables = null,
    Dictionary<string, JsonElement?>? Defaults = null,
    Dictionary<string, string>? Artifacts = null,
    ApprovalConfig? Approval = null);
