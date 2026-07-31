using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Api;

public static class AttachmentRoutes
{
    public static WebApplication MapAttachmentRoutes(this WebApplication app)
    {
        var projects = app.MapGroup("/api/projects/{projectRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        projects.MapPost("/attachments", async (
            HttpContext ctx,
            [FromRoute] string projectRef,
            AttachmentService attachments) =>
        {
            var project = IssueRoutes.GetRequiredProject(ctx);
            if (!ctx.Request.HasFormContentType)
                return ApiResults.BadRequest("multipart/form-data is required", "invalid_multipart");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
                return ApiResults.BadRequest("attachment file is required", "missing_file");

            try
            {
                var upload = await attachments.UploadAsync(project.Id, file, ctx.RequestAborted);
                return ApiResults.Ok(new AttachmentUploadResponse(
                    upload.Id,
                    upload.FileName,
                    upload.ContentType,
                    upload.Size,
                    upload.ExpiresAt?.ToString("o")));
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.Fail(ex.Message, 413, "attachment_size_limit_exceeded");
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_attachment");
            }
            catch (AttachmentStorageException ex)
            {
                return ApiResults.Fail(ex.Message, 500, "attachment_storage_error");
            }
        });

        projects.MapGet("/issues/{number:int}/attachments/{attachmentId}/content", async (
            HttpContext ctx,
            [FromRoute] string projectRef,
            int number,
            string attachmentId,
            IssueQuerier issuesQuery,
            AttachmentService attachments) =>
        {
            var project = IssueRoutes.GetRequiredProject(ctx);
            var issue = await issuesQuery.GetAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            try
            {
                var content = await attachments.OpenIssueContentAsync(project.Id, number, attachmentId, ctx.RequestAborted);
                return content is null ? ApiResults.NotFound("Attachment not found") : StreamAttachment(ctx, content);
            }
            catch (AttachmentNotFoundException)
            {
                return ApiResults.NotFound("Recorded attachment content is missing");
            }
            catch (AttachmentStorageException ex)
            {
                return ApiResults.Fail(ex.Message, 500, "attachment_storage_error");
            }
        });

        projects.MapGet("/issues/{number:int}/comments/{commentId}/attachments/{attachmentId}/content", async (
            HttpContext ctx,
            [FromRoute] string projectRef,
            int number,
            string commentId,
            string attachmentId,
            IssueQuerier issuesQuery,
            AttachmentService attachments) =>
        {
            var project = IssueRoutes.GetRequiredProject(ctx);
            var issue = await issuesQuery.GetAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                var content = await attachments.OpenCommentContentAsync(project.Id, number, commentId, attachmentId, ctx.RequestAborted);
                return content is null ? ApiResults.NotFound("Attachment not found") : StreamAttachment(ctx, content);
            }
            catch (AttachmentNotFoundException)
            {
                return ApiResults.NotFound("Recorded attachment content is missing");
            }
            catch (AttachmentStorageException ex)
            {
                return ApiResults.Fail(ex.Message, 500, "attachment_storage_error");
            }
        });

        projects.MapGet("/agent-sessions/{sessionId}/inputs/{inputId}/attachments/{attachmentId}/content", async (
            HttpContext ctx,
            [FromRoute] string projectRef,
            string sessionId,
            string inputId,
            string attachmentId,
            AttachmentService attachments) =>
        {
            var project = IssueRoutes.GetRequiredProject(ctx);
            try
            {
                var content = await attachments.OpenAgentInputContentAsync(
                    project.Id, sessionId, inputId, attachmentId, ctx.RequestAborted);
                return content is null ? ApiResults.NotFound("Attachment not found") : StreamAttachment(ctx, content);
            }
            catch (AttachmentNotFoundException)
            {
                return ApiResults.NotFound("Recorded attachment content is missing");
            }
            catch (AttachmentStorageException ex)
            {
                return ApiResults.Fail(ex.Message, 500, "attachment_storage_error");
            }
        });

        projects.MapDelete("/issues/{number:int}/attachments/{attachmentId}", async (
            HttpContext ctx,
            [FromRoute] string projectRef,
            int number,
            string attachmentId,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            AttachmentService attachments) =>
        {
            var project = IssueRoutes.GetRequiredProject(ctx);
            var issue = await issuesQuery.GetDomainAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");
            var grain = await IssueRoutes.GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");

            try
            {
                var result = await attachments.RemoveIssueAttachmentAsync(project.Id, issue, attachmentId, grain, ctx.RequestAborted);
                return result == AttachmentRemovalResult.NotFound ? ApiResults.NotFound("Attachment not found") : ApiResults.Ok();
            }
            catch (AttachmentEditabilityException ex)
            {
                return ApiResults.Conflict(ex.Message, "attachment_owner_not_editable");
            }
        });

        projects.MapDelete("/issues/{number:int}/comments/{commentId}/attachments/{attachmentId}", async (
            HttpContext ctx,
            [FromRoute] string projectRef,
            int number,
            string commentId,
            string attachmentId,
            AttachmentService attachments) =>
        {
            var project = IssueRoutes.GetRequiredProject(ctx);

            try
            {
                var result = await attachments.RemoveCommentAttachmentAsync(project.Id, number, commentId, attachmentId, ctx.RequestAborted);
                return result == AttachmentRemovalResult.NotFound ? ApiResults.NotFound("Attachment not found") : ApiResults.Ok();
            }
            catch (AttachmentEditabilityException ex)
            {
                return ApiResults.Conflict(ex.Message, "attachment_owner_not_editable");
            }
        });

        return app;
    }

    private static IResult StreamAttachment(HttpContext ctx, AttachmentContentResult content)
    {
        ctx.Response.Headers.ContentDisposition = content.ContentDisposition;
        return Results.Stream(content.Content, content.ContentType);
    }
}
