using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkflowRunPhase { Pending, Running, AwaitingApproval, Paused, Completed, Failed }

public sealed record WorkflowRunMetadata(
    string? Name,
    DateTimeOffset CreatedAt,
    Dictionary<string, string>? Labels = null,
    Dictionary<string, string>? Annotations = null);

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required WorkflowRunMetadata Metadata { get; set; }
    public WorkflowRunPhase Phase { get; set; }
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
                    Phase = StageRunPhase.Pending
                })
                .ToList();

            return new WorkflowRun
            {
                Id = id,
                Metadata = metadata ?? new WorkflowRunMetadata(null, DateTimeOffset.UtcNow),
                Phase = WorkflowRunPhase.Pending,
                CurrentStageId = stages[0].StageId,
                Stages = stages
            };
        }
    }

    extension(WorkflowRun run)
    {
        public void Start()
        {
            if (run.Phase != WorkflowRunPhase.Pending && run.Phase != WorkflowRunPhase.Paused)
                throw new WorkflowDomainException($"WorkflowRun is {run.Phase}");

            var current = run.CurrentStage();
            if (current.Phase == StageRunPhase.Pending)
                current.Phase = StageRunPhase.Running;

            run.Phase = WorkflowRunPhase.Running;
            run.StartedAt ??= DateTimeOffset.UtcNow;
        }

        public void Pause()
        {
            if (run.Phase != WorkflowRunPhase.Running)
                throw new WorkflowDomainException($"WorkflowRun is {run.Phase}, pause requires Running");
            run.Phase = WorkflowRunPhase.Paused;
        }

        public void Resume()
        {
            if (run.Phase != WorkflowRunPhase.Paused)
                throw new WorkflowDomainException($"WorkflowRun is {run.Phase}, resume requires Paused");

            var current = run.CurrentStage();
            if (current.Phase == StageRunPhase.Pending)
                current.Phase = StageRunPhase.Running;

            run.Phase = WorkflowRunPhase.Running;
        }
    }
}
