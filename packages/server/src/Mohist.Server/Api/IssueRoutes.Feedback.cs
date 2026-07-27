using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueFeedback(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/feedback", async (
            HttpContext ctx,
            string projectRef,
            int number,
            CreateFeedbackRequest req,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            if (string.IsNullOrWhiteSpace(req.Stage))
                return ApiResults.BadRequest("stage is required");
            if (string.IsNullOrWhiteSpace(req.Body))
                return ApiResults.BadRequest("body is required");

            var project = GetRequiredProject(ctx);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");

            try
            {
                string? decidedBy;
                try
                {
                    decidedBy = ApprovalOperatorValidation.Normalize(req.Author);
                }
                catch (ArgumentException ex)
                {
                    return ApiResults.BadRequest(ex.Message);
                }
                var feedbackId = await grains.GetGrain<IWorkflowGrain>(wrId).RequestChangesAsync(req.Body, decidedBy);
                var feedback = await grains.GetGrain<IWorkflowGrain>(wrId).GetFeedbackAsync(feedbackId);
                if (feedback is null)
                    return ApiResults.NotFound("Feedback was created but could not be read back");
                return Results.Json(new { success = true, data = feedback }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapGet("/{number:int}/feedback", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string? stage,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.Ok(Array.Empty<WorkflowFeedbackRecord>());

            var all = await grains.GetGrain<IWorkflowGrain>(wrId).ListFeedbackAsync();
            IReadOnlyList<WorkflowFeedbackRecord> filtered = string.IsNullOrWhiteSpace(stage)
                ? all
                : all.Where(f => string.Equals(f.Stage, stage, StringComparison.OrdinalIgnoreCase)).ToList();
            return ApiResults.Ok(filtered);
        });

        group.MapGet("/{number:int}/feedback/{feedbackId}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            string feedbackId,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");

            var feedback = await grains.GetGrain<IWorkflowGrain>(wrId).GetFeedbackAsync(feedbackId);
            return feedback is null
                ? ApiResults.NotFound($"Feedback '{feedbackId}' not found")
                : ApiResults.Ok(feedback);
        });
    }
}
