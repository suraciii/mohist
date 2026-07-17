using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static partial class WorkflowRoutes
{
    public static WebApplication MapWorkflowTaskRoutes(this WebApplication app)
    {
        app.MapGet("/api/workflow-runs/{workflowRunId}/yaml", async (
            string workflowRunId,
            WorkflowQuerier reader) =>
        {
            if (await EnsureWorkflowRunExistsAsync(workflowRunId, reader) is { } failure)
                return failure;

            var yaml = await reader.GetDefinitionYamlAsync(workflowRunId);
            return yaml is null
                ? ApiResults.NotFound("Workflow definition not found")
                : ApiResults.Ok(new { workflowRunId, yaml });
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/variables/effective", async (
            string workflowRunId,
            string? stage,
            WorkflowQuerier reader) =>
        {
            if (await EnsureWorkflowRunExistsAsync(workflowRunId, reader) is { } failure)
                return failure;

            return ApiResults.Ok(await reader.GetEffectiveVariablesAsync(workflowRunId, stage));
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/variables/effective/{*keyPath}", async (
            string workflowRunId,
            string keyPath,
            string? stage,
            WorkflowQuerier reader) =>
        {
            if (await EnsureWorkflowRunExistsAsync(workflowRunId, reader) is { } failure)
                return failure;

            return ApiResults.Ok(await reader.GetEffectiveVariableAsync(workflowRunId, keyPath, stage));
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/tasks", async (
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
                request.InvalidateChecks,
                Expect: request.Expect));

            return ApiResults.Ok(new { result.WorkflowRunId, result.Stage, result.TaskId });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/tasks/batch", async (
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
                t.With,
                t.Expect)).ToList();

            var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
            var result = await workflow.AddTasksAsync(new AddTasksBatchRequest(items));

            return ApiResults.Ok(new { result.WorkflowRunId, result.Stage, result.AddedCount });
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/workflow-profile", async (
            string workflowRunId,
            WorkflowRunProfileManager runProfileManager) =>
        {
            var variables = await runProfileManager.GetVariablesAsync(workflowRunId);
            return ApiResults.Ok(new { workflowRunId, variables });
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/workflow-profile/variables", async (
            string workflowRunId,
            WorkflowRunProfileManager runProfileManager) =>
        {
            return ApiResults.Ok(await runProfileManager.GetVariablesAsync(workflowRunId));
        });

        app.MapPut("/api/workflow-runs/{workflowRunId}/workflow-profile/variables", async (
            string workflowRunId,
            VariableBundle bundle,
            WorkflowRunProfileManager runProfileManager) =>
        {
            return ApiResults.Ok(await runProfileManager.SetVariablesAsync(workflowRunId, bundle));
        });

        app.MapPatch("/api/workflow-runs/{workflowRunId}/workflow-profile/variables", async (
            string workflowRunId,
            VariableBundle patch,
            WorkflowRunProfileManager runProfileManager) =>
        {
            return ApiResults.Ok(await runProfileManager.PatchVariablesAsync(workflowRunId, patch));
        });

        return app;
    }

    public sealed record AddTaskRequestDto(
        string? Id,
        string? Title,
        string? Uses,
        JsonElement? With,
        string? Stage,
        bool InvalidateChecks = false,
        JsonElement? Expect = null);
    public sealed record AddTasksRequestDto(IReadOnlyList<AddTasksRequestTaskDto>? Tasks);
    public sealed record AddTasksRequestTaskDto(
        string? Id,
        string? Title,
        string? Uses,
        JsonElement? With,
        JsonElement? Expect = null);

    internal static async Task<IResult?> EnsureWorkflowRunExistsAsync(string workflowRunId, WorkflowQuerier reader)
    {
        var status = await reader.GetStatusAsync(workflowRunId);
        return status is null
            ? ApiResults.NotFound($"Workflow run '{workflowRunId}' not found")
            : null;
    }
}
