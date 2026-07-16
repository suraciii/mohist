using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueLifecycle(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/start", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                await grain.StartWorkAsync();
                return ApiResults.Ok();
            }
            catch (IssueStartBlockedException ex)
            {
                var blockerDto = IssueStartBlockerDto.FromDomain(ex.Blocker);
                var code = ex.Blocker switch
                {
                    IssueStartBlocker.Draft => "draft",
                    IssueStartBlocker.WaitingFor => "waiting_for_prerequisite",
                    _ => "start_blocked",
                };
                return ApiResults.Fail(
                    ex.Message,
                    400,
                    code,
                    new
                    {
                        canStart = false,
                        blocker = blockerDto,
                    });
            }
            catch (MissingPromptsException ex)
            {
                return ApiResults.Fail(ex.Message, 400, "missing_prompts", new { missingKeys = ex.MissingKeys });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPost("/{number:int}/comments", async (
            HttpContext ctx,
            string projectRef,
            int number,
            AddCommentRequest req,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            IssueCommentResult comment;
            try
            {
                comment = await grain.AddCommentAsync(req.Body, req.AttachmentIds);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.Fail(ex.Message, 413, "attachment_count_limit_exceeded");
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_attachment");
            }
            return Results.Json(new { success = true, data = new { id = comment.Id, body = comment.Body } });
        });

        group.MapPost("/{number:int}/close", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            await grain.CancelAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/reopen", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                await grain.ReopenAsync();
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPost("/{number:int}/archive", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var issue = await issuesQuery.GetAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is { } repoError) return repoError;

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                await grain.ArchiveAsync();
                return ApiResults.Ok(new
                {
                    message = "Issue archived",
                });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPost("/{number:int}/unarchive", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            await grain.UnarchiveAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/archive-completed", async (
            HttpContext ctx,
            string projectRef,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var all = await issuesQuery.ListAsync(project.Id, null, all: true);
            var completed = all.Where(i => i.Status == "done" && i.ArchivedAt == null).ToList();
            var skipped = all.Where(i => i.Status != "done" && i.ArchivedAt == null).ToList();

            foreach (var issue in completed)
            {
                var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
                try
                {
                    if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is not null)
                    {
                        continue;
                    }
                    await grain.ArchiveAsync();
                }
                catch
                {
                    /* skip if already archived */
                }
            }

            return ApiResults.Ok(new
            {
                archived = completed.Count,
                skipped = skipped.Count,
                skippedNumbers = skipped.Select(s => s.Number).ToList(),
                message = $"Archived {completed.Count} completed issues, skipped {skipped.Count}"
            });
        });
    }
}
