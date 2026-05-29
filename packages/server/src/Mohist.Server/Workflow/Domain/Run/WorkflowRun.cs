using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Errors;

namespace Mohist.Server.Workflow.Domain.Run;

public record WorkflowRun(
    string Id,
    WorkflowRunMetadata Metadata,
    WorkflowRunPhase Phase,
    string? CurrentStageId,
    IReadOnlyList<StageRun> Stages,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    FailureDetails? Failure);
