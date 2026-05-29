using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

public enum StageRunPhase { Pending, Running, AwaitingApproval, Completed, Failed }

public sealed record ApprovalState(
    string Status,
    JsonElement? Output,
    string RequestedAt,
    string? RespondedAt);

public sealed class StageRun
{
    public required string StageId { get; init; }
    public required int Order { get; init; }
    public required int Attempt { get; init; }
    public required bool RequiresApproval { get; init; }
    public StageRunPhase Phase { get; set; }
    public bool Initialized { get; set; }
    public string? TasksFrom { get; set; }
    public List<TaskRun> Tasks { get; set; } = new();
    public List<StageCheck> Checks { get; set; } = new();
    public ApprovalState? Approval { get; set; }
    public FailureDetails? Failure { get; set; }
}
