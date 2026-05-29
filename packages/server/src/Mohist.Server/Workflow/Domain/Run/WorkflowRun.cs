using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkflowRunStatus { Pending, Running, AwaitingApproval, Paused, Completed, Failed }

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

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun)
    {
        public static WorkflowRun Create(
            string id,
            WorkflowDefinition definition,
            WorkflowRunMetadata? metadata = null)
        {
            if (definition.Stages.Count == 0)
                throw new WorkflowDomainException("WorkflowDefinition requires at least one stage");

            var stages = definition.Stages
                .Select((def, i) => new StageRun
                {
                    StageId = def.Stage,
                    Order = i,
                    Attempt = 1,
                    RequiresApproval = def.RequiresApproval,
                    Status = StageRunStatus.Pending
                })
                .ToList();

            return new WorkflowRun
            {
                Id = id,
                Metadata = metadata ?? new WorkflowRunMetadata(null, DateTimeOffset.UtcNow),
                Status = WorkflowRunStatus.Pending,
                CurrentStageId = stages[0].StageId,
                Stages = stages
            };
        }
    }

    extension(WorkflowRun run)
    {
        public void Start()
        {
            if (run.Status != WorkflowRunStatus.Pending && run.Status != WorkflowRunStatus.Paused)
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}");

            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
            run.StartedAt ??= DateTimeOffset.UtcNow;
        }

        public void Pause()
        {
            if (run.Status != WorkflowRunStatus.Running)
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}, pause requires Running");
            run.Status = WorkflowRunStatus.Paused;
        }

        public void Resume()
        {
            if (run.Status != WorkflowRunStatus.Paused)
                throw new WorkflowDomainException($"WorkflowRun is {run.Status}, resume requires Paused");

            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            run.Status = WorkflowRunStatus.Running;
        }
    }
}
