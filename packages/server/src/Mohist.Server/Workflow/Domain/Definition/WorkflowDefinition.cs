using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Domain.Definition;

[GenerateSerializer]
public sealed record TaskArtifactDeclaration(string Path);

[GenerateSerializer]
public sealed record TaskArtifactCapture(List<TaskArtifactDeclaration> Files)
{
    public bool IsEmpty => Files is null || Files.Count == 0;
}

public sealed record TaskDefinition(
    string Id,
    string Title,
    string? Uses = null,
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
    string Name,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);

public sealed record StageDefinition(
    string Stage,
    List<TaskDefinition> Tasks,
    List<CheckDefinition> Checks,
    bool RequiresApproval = false,
    string? LockBehavior = null,
    List<string>? Resources = null)
{
    public StageDefinition(
        string Stage,
        List<TaskDefinition> Tasks,
        List<CheckDefinition> Checks,
        Dictionary<string, JsonElement?>? Variables,
        string? LockBehavior = null,
        List<string>? Resources = null)
        : this(Stage, Tasks, Checks, false, LockBehavior, Resources)
    {
    }

    public StageDefinition(
        string Stage,
        List<TaskDefinition> Tasks,
        List<CheckDefinition> Checks,
        bool RequiresApproval,
        Dictionary<string, JsonElement?>? Variables,
        string? LockBehavior = null,
        List<string>? Resources = null)
        : this(Stage, Tasks, Checks, RequiresApproval, LockBehavior, Resources)
    {
    }
}

public sealed record ApprovalFeedbackConfig(IReadOnlyList<TaskDefinition>? Tasks = null);

public sealed record ApprovalConfig(ApprovalFeedbackConfig? Feedback = null);

/// <summary>
/// Narrow view of a workflow's structure for the control-plane create path:
/// the stage sequence with each stage's <see cref="RequiresApproval"/> flag.
/// Carries no tasks, checks, lock behavior, or other per-stage detail — those
/// are loaded separately via <c>LoadStageSpecsAsync</c>. <see cref="Definition"/>
/// stays an internal implementation detail of <c>WorkflowProfileManager</c>;
/// the grain only ever consumes this projection.
/// </summary>
public sealed record WorkflowStructure(
    string Id,
    List<StageStructure> Stages);

public sealed record StageStructure(
    string Stage,
    bool RequiresApproval);

public sealed record WorkflowDefinition(
    List<StageDefinition> Stages,
    ApprovalConfig? Approval = null)
{
    // Kept only so older in-process callers can be migrated independently. The
    // value is deliberately not part of the Definition JSON contract.
    [JsonIgnore]
    public string Id { get; init; } = "workflow";

    [JsonIgnore] public string? Name { get; init; }
    [JsonIgnore] public string? Description { get; init; }
    [JsonIgnore] public Dictionary<string, JsonElement?>? Variables { get; init; }
    [JsonIgnore] public Dictionary<string, JsonElement?>? Defaults { get; init; }
    [JsonIgnore] public Dictionary<string, string>? Artifacts { get; init; }

    public WorkflowDefinition(
        string Id,
        List<StageDefinition> Stages,
        string? Name = null,
        string? Description = null,
        Dictionary<string, JsonElement?>? Variables = null,
        Dictionary<string, JsonElement?>? Defaults = null,
        Dictionary<string, string>? Artifacts = null,
        ApprovalConfig? Approval = null)
        : this(Stages, Approval)
    {
        this.Id = Id;
        this.Name = Name;
        this.Description = Description;
        this.Variables = Variables;
        this.Defaults = Defaults;
        this.Artifacts = Artifacts;
    }

    public WorkflowStructure ToStructure() => new(
        Id,
        Stages?.Select(s => new StageStructure(s.Stage, s.RequiresApproval)).ToList() ?? new List<StageStructure>());
}

public sealed record WorkflowProfile(
    string Id,
    string Name,
    string Description,
    WorkflowDefinition Definition);
