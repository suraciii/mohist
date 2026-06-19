using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain;
using Orleans;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// The lifecycle status of a <see cref="WorkflowRun"/> aggregate.
/// This state machine is independent from <see cref="TaskRunStatus"/> —
/// the two describe different aggregates and do not derive each other.
/// <c>Paused</c>, <c>Stopped</c>, and <c>AwaitingApproval</c> only result
/// from workflow-level commands, never from a task status transition.
/// </summary>
public enum WorkflowRunStatus { Pending, Running, AwaitingApproval, Paused, Stopped, Completed, Failed }

[GenerateSerializer]
public sealed record WorkflowRunMetadata(
    [property: Id(0)] string? Name,
    [property: Id(1)] DateTimeOffset CreatedAt,
    [property: Id(2)] Dictionary<string, string>? Labels = null,
    [property: Id(3)] Dictionary<string, string>? Annotations = null);

[GenerateSerializer]
public sealed record WorkspaceIdentity(
    [property: Id(0)] string Path,
    [property: Id(1)] string? Branch = null,
    [property: Id(2)] string? ChangeDir = null);

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required WorkflowRunMetadata Metadata { get; set; }
    public WorkflowRunStatus Status { get; set; }
    /// <summary>
    /// The single-runner invariant: at most one runner may claim this run
    /// for its entire lifecycle. Once a <see cref="WorkflowClaimInfo"/> exists,
    /// its <c>RunnerId</c> is the unique runner identity for this run.
    /// A <c>Running</c> <see cref="TaskRun"/>'s <c>RunnerId</c> equals
    /// <c>Claim.RunnerId</c> as a consequence of this invariant, not as
    /// an independently-kept-in-sync fact.
    /// </summary>
    public WorkflowClaimInfo? Claim { get; set; }
    public string? CurrentStageId { get; set; }
    public required List<StageRun> Stages { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public FailureDetails? Failure { get; set; }
    public WorkspaceIdentity? Workspace { get; set; }
    public DateTimeOffset? WorkspaceMaterializedAt { get; set; }
    public Dictionary<string, JsonElement> RuntimeVariables { get; init; } = new(StringComparer.Ordinal);
    public List<ApprovalFeedback> Feedback { get; set; } = new();

    public bool IsClaimed => Claim is not null;
    public string? ClaimedBy => Claim?.RunnerId;
}
