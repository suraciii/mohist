using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static partial class WorkflowRoutes
{
    public static WebApplication MapWorkflowRunControlRoutes(this WebApplication app)
    {
        app.MapPost("/api/workflow-runs/{workflowRunId}/resume", async (
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader) =>
        {
            if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                return failure;
            await grains.GetGrain<IWorkflowGrain>(workflowRunId).ResumeAsync();
            return ApiResults.Ok();
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/approve", async (
            string workflowRunId,
            ApproveRequest? req,
            IGrainFactory grains,
            WorkflowQuerier reader) =>
        {
            if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                return failure;
            string? decidedBy;
            try
            {
                decidedBy = ApprovalOperatorValidation.Normalize(req?.Author);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
            await grains.GetGrain<IWorkflowGrain>(workflowRunId).ApproveAsync(decidedBy);
            return ApiResults.Ok();
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/reject", async (
            string workflowRunId,
            RejectWithAuthorRequest? req,
            IGrainFactory grains,
            WorkflowQuerier reader) =>
        {
            if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                return failure;
            string? decidedBy;
            try
            {
                decidedBy = ApprovalOperatorValidation.Normalize(req?.Author);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
            if (string.IsNullOrWhiteSpace(req?.Message))
                return ApiResults.BadRequest("Reject reason is required");
            await grains.GetGrain<IWorkflowGrain>(workflowRunId).RequestChangesAsync(req.Message, decidedBy);
            return ApiResults.Ok();
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/retry", async (
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader) =>
        {
            if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.RetryOrRerun) is { } failure)
                return failure;
            await grains.GetGrain<IWorkflowGrain>(workflowRunId).RetryAsync();
            return ApiResults.Ok();
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/rerun", async (
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader,
            IssueQuerier issuesQuery) =>
        {
            try
            {
                if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.RetryOrRerun) is { } failure)
                    return failure;
                await grains.GetGrain<IWorkflowGrain>(workflowRunId).RerunAsync();
                return ApiResults.Ok();
            }
            catch (Exception ex) when (WorkflowControlRecovery.IsWorkflowRunStateCorruption(ex))
            {
                return await WorkflowControlRecovery.RecoverWorkflowRunScopedRerunAsync(grains, issuesQuery, workflowRunId);
            }
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/rerun-from-stage", async (
            string workflowRunId,
            RerunFromStageRequest? req,
            IGrainFactory grains,
            WorkflowQuerier reader,
            IssueQuerier issuesQuery) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Stage))
                return ApiResults.BadRequest("Stage is required for rerun-from-stage");
            try
            {
                if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.RetryOrRerun) is { } failure)
                    return failure;
                var result = await grains.GetGrain<IWorkflowGrain>(workflowRunId).RerunFromStageAsync(req.Stage);
                if (!result.Success)
                {
                    return result.Code switch
                    {
                        "unknown_stage" or "stage_not_reached" => ApiResults.BadRequest(result.Error ?? "Workflow control rejected", result.Code, result.Details),
                        "active_work_in_range" => ApiResults.Conflict(result.Error ?? "Workflow control rejected", result.Code, result.Details),
                        _ => ApiResults.BadRequest(result.Error ?? "Workflow control rejected", result.Code, result.Details),
                    };
                }
                return ApiResults.Ok();
            }
            catch (Exception ex) when (WorkflowControlRecovery.IsWorkflowRunStateCorruption(ex))
            {
                return await WorkflowControlRecovery.RecoverWorkflowRunScopedRerunAsync(grains, issuesQuery, workflowRunId);
            }
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/pause", async (
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader) =>
        {
            if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                return failure;
            await grains.GetGrain<IWorkflowGrain>(workflowRunId).PauseAsync("user-pause");
            return ApiResults.Ok();
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/stop", async (
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader) =>
        {
            if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.Stop) is { } failure)
                return failure;
            try
            {
                await grains.GetGrain<IWorkflowGrain>(workflowRunId).StopAsync("user-stop");
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        return app;
    }

    internal sealed record RerunFromStageRequest(string? Stage);

    private static async Task<IResult?> ResolveWorkflowRunControlAsync(
        string workflowRunId,
        WorkflowQuerier reader,
        WorkflowControlAction action)
    {
        var status = await reader.GetStatusAsync(workflowRunId);
        if (status is null) return ApiResults.NotFound($"Workflow run '{workflowRunId}' not found");
        if (!WorkflowControlGuard.IsWorkflowControllableForAction(status.Status, action))
            return ApiResults.Conflict("Workflow is not active for this run");
        return null;
    }
}
