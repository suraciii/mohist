using Microsoft.AspNetCore.Routing;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueWorkflowControl(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/resume", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.ActiveOnly);
            if (control.Result is not null) return control.Result;
            var wrId = control.WorkflowRunId!;
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/approve", async (
            HttpContext ctx,
            string projectRef,
            int number,
            ApproveRequest? req,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ICurrentUser currentUser) =>
        {
            var project = GetRequiredProject(ctx);
            var decidedBy = currentUser.Principal.Id;
            var displayName = NormalizeDisplayName(req?.DisplayName);
            if (displayName.Failure is { } failure)
                return failure;
            var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.ActiveOnly);
            if (control.Result is not null) return control.Result;
            var wrId = control.WorkflowRunId!;
            await grains.GetGrain<IWorkflowGrain>(wrId).ApproveAsync(decidedBy, displayName.Value);
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/reject", async (
            HttpContext ctx,
            string projectRef,
            int number,
            RejectWithAuthorRequest? req,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ICurrentUser currentUser) =>
        {
            var project = GetRequiredProject(ctx);
            var decidedBy = currentUser.Principal.Id;
            var displayName = NormalizeDisplayName(req?.DisplayName);
            if (displayName.Failure is { } failure)
                return failure;
            if (string.IsNullOrWhiteSpace(req?.Message))
                return ApiResults.BadRequest("Reject reason is required");
            var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.ActiveOnly);
            if (control.Result is not null) return control.Result;
            var wrId = control.WorkflowRunId!;
            await grains.GetGrain<IWorkflowGrain>(wrId).RequestChangesAsync(req.Message, decidedBy, displayName.Value);
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/retry", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.RetryOrRerun);
            if (control.Result is not null) return control.Result;
            var wrId = control.WorkflowRunId!;
            await grains.GetGrain<IWorkflowGrain>(wrId).RetryAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/rerun", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            try
            {
                var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.RetryOrRerun);
                if (control.Result is not null) return control.Result;
                var wrId = control.WorkflowRunId!;
                await grains.GetGrain<IWorkflowGrain>(wrId).RerunAsync();
            }
            catch (Exception ex) when (WorkflowControlRecovery.IsWorkflowRunStateCorruption(ex))
            {
                return await WorkflowControlRecovery.RecoverIssueScopedRerunAsync(grains, issuesQuery, project.Id, number);
            }
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/rerun-from-stage", async (
            HttpContext ctx,
            string projectRef,
            int number,
            RerunFromStageRequest? req,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            if (string.IsNullOrWhiteSpace(req?.Stage))
                return ApiResults.BadRequest("Stage is required for rerun-from-stage");
            try
            {
                var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.RetryOrRerun);
                if (control.Result is not null) return control.Result;
                var wrId = control.WorkflowRunId!;
                var result = await grains.GetGrain<IWorkflowGrain>(wrId).RerunFromStageAsync(req.Stage);
                if (!result.Success)
                {
                    return result.Code switch
                    {
                        "unknown_stage" or "stage_not_reached" => ApiResults.BadRequest(result.Error ?? "Workflow control rejected", result.Code, result.Details),
                        "active_work_in_range" => ApiResults.Conflict(result.Error ?? "Workflow control rejected", result.Code, result.Details),
                        _ => ApiResults.BadRequest(result.Error ?? "Workflow control rejected", result.Code, result.Details),
                    };
                }
            }
            catch (Exception ex) when (WorkflowControlRecovery.IsWorkflowRunStateCorruption(ex))
            {
                return await WorkflowControlRecovery.RecoverIssueScopedRerunAsync(grains, issuesQuery, project.Id, number);
            }
            return ApiResults.Ok();
        });

        // Force-stop is implemented as workflow pause. The user can resume afterwards.
        // For terminal disposal, use /close (issue close -> workflow Stopped) or /stop.
        group.MapPost("/{number:int}/force-stop", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.ActiveOnly);
            if (control.Result is not null) return control.Result;
            var wrId = control.WorkflowRunId!;
            await grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("user-force-stop");
            return ApiResults.Ok();
        });

        // Stop is a terminal pause: the workflow run is permanently stopped (cannot be resumed).
        // The issue itself is NOT closed; the user can re-open or close it separately.
        group.MapPost("/{number:int}/stop", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var control = await ResolveWorkflowControlAsync(project.Id, number, issuesQuery, grains, WorkflowControlAction.Stop);
            if (control.Result is not null) return control.Result;
            var wrId = control.WorkflowRunId!;
            try
            {
                await grains.GetGrain<IWorkflowGrain>(wrId).StopAsync("user-stop");
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });
    }

    private static async Task<WorkflowControlResolution> ResolveWorkflowControlAsync(
        string projectId,
        int number,
        IssueQuerier issuesQuery,
        IGrainFactory grains,
        WorkflowControlAction action)
    {
        var issue = await issuesQuery.GetInfoAsync(projectId, number);
        if (issue is null) return new(null, ApiResults.NotFound($"Issue #{number} not found"));
        if (issue.WorkflowRunId is null) return new(null, ApiResults.NotFound("No workflow run"));
        if (issue.Status is not "in_progress" and not "in-progress")
            return new(null, ApiResults.Conflict("Workflow is not active for this issue"));

        var issueGrain = await GetIssueGrainAsync(grains, issuesQuery, projectId, number);
        if (issueGrain is null) return new(null, ApiResults.NotFound($"Issue #{number} not found"));

        var workflow = await issueGrain.GetWorkflowStatusAsync();
        var workflowStatus = workflow?.Workflow?.Status;
        if (!IsWorkflowControllableForAction(workflowStatus, action))
            return new(null, ApiResults.Conflict("Workflow is not active for this issue"));

        return new(issue.WorkflowRunId, null);
    }

    private static bool IsWorkflowControllableForAction(string? workflowStatus, WorkflowControlAction action) =>
        WorkflowControlGuard.IsWorkflowControllableForAction(workflowStatus, action);

    internal sealed record RerunFromStageRequest(string? Stage);

    private static DisplayNameResult NormalizeDisplayName(string? raw)
    {
        try
        {
            return new DisplayNameResult(ApprovalOperatorValidation.Normalize(raw), null);
        }
        catch (ArgumentException ex)
        {
            return new DisplayNameResult(null, ApiResults.BadRequest(ex.Message));
        }
    }

    private sealed record DisplayNameResult(string? Value, IResult? Failure);

    private sealed record WorkflowControlResolution(string? WorkflowRunId, IResult? Result);
}
