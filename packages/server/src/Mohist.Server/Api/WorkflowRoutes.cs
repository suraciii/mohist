using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static class WorkflowRoutes
{
    public static WebApplication MapWorkflowTaskRoutes(this WebApplication app)
    {
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

    public sealed record AddTasksRequestDto(IReadOnlyList<AddTasksRequestTaskDto>? Tasks);
    public sealed record AddTasksRequestTaskDto(string? Id, string? Title, string? Uses, string? With);
}
