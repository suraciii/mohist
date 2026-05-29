namespace Mohist.Server.Workflow.Domain.Run;

public sealed record WorkflowRunState(
    WorkflowRunSnapshot Run,
    string? ProjectId);
