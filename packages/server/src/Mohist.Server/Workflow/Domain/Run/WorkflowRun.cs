using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkflowRunStatus { Pending, Running, AwaitingApproval, Paused, Stopped, Completed, Failed }

public sealed record WorkflowRunMetadata(
    string? Name,
    DateTimeOffset CreatedAt,
    Dictionary<string, string>? Labels = null,
    Dictionary<string, string>? Annotations = null);

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required WorkflowRunMetadata Metadata { get; set; }
    public WorkflowRunStatus Status { get; set; }
    public string? CurrentStageId { get; set; }
    public required List<StageRun> Stages { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public FailureDetails? Failure { get; set; }
}
