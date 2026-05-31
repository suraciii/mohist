using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static class WorkflowRoutes
{
    public static WebApplication MapWorkflowTaskRoutes(this WebApplication app)
    {
        app.MapPost("/api/workflows/{workflowRunId}/tasks", async (
            string workflowRunId,
            AddTaskRequestDto request,
            IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id))
                return ApiResults.BadRequest("Task id is required");
            if (string.IsNullOrWhiteSpace(request.Title))
                return ApiResults.BadRequest("Task title is required");

            var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
            var result = await workflow.AddTaskAsync(new RuntimeTaskInput(
                request.Id,
                request.Title,
                request.Uses,
                request.With,
                request.Stage,
                request.InvalidateChecks));

            return ApiResults.Ok(new { result.WorkflowRunId, result.Stage, result.TaskId });
        });

        app.MapPost("/api/workflow/{workflowRunId}/tasks", async (
            string workflowRunId,
            AddTasksRequestDto request,
            IGrainFactory grains) =>
        {
            if (request.Tasks is null || request.Tasks.Count == 0)
                return ApiResults.BadRequest("At least one task is required");

            var items = request.Tasks.Select(t => new AddTasksBatchItem(
                t.Id ?? throw new InvalidOperationException("Task id is required"),
                t.Title ?? throw new InvalidOperationException("Task title is required"),
                t.Uses,
                t.With)).ToList();

            var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
            var result = await workflow.AddTasksAsync(new AddTasksBatchRequest(items));

            return ApiResults.Ok(new { result.WorkflowRunId, result.Stage, result.AddedCount });
        });

        return app;
    }

    public sealed record AddTaskRequestDto(string? Id, string? Title, string? Uses, string? With, string? Stage, bool InvalidateChecks = false);
    public sealed record AddTasksRequestDto(IReadOnlyList<AddTasksRequestTaskDto>? Tasks);
    public sealed record AddTasksRequestTaskDto(string? Id, string? Title, string? Uses, string? With);
}
