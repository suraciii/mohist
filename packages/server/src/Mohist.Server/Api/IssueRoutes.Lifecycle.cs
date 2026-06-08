using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
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
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                var eligibility = await grain.GetStartEligibilityAsync();
                if (!eligibility.Startable)
                    return ApiResults.Conflict(eligibility.Message ?? "Issue is waiting for prerequisites", "start_blocked", eligibility);

                await grain.StartWorkAsync();
                return ApiResults.Ok();
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
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            var comment = await grain.AddCommentAsync(req.Body);
            return Results.Json(new { success = true, data = new { id = comment.Id, body = comment.Body } });
        });

        group.MapPost("/{number:int}/close", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            await grain.CancelAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/reopen", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
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
            IssueIdentityResolver issueIdentityResolver,
            IGitService git,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var issue = await issuesQuery.GetAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is { } repoError) return repoError;

            var repoPath = issue.Repository!.Path ?? IssueRepositoryResolutionHelpers.DefaultRepoPath;
            var projectName = issue.ProjectName ?? "project";

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                await grain.ArchiveAsync();
                var cleanup = await git.RemoveWorktreeAsync(repoPath, projectName, number);
                return ApiResults.Ok(new
                {
                    message = cleanup.Removed
                        ? "Issue archived and worktree removed"
                        : "Issue archived",
                    cleanup,
                    warning = cleanup.Status == "failed" ? cleanup.Message : null,
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
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            await grain.UnarchiveAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/archive-completed", async (
            HttpContext ctx,
            string projectRef,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            IGitService git) =>
        {
            var project = GetRequiredProject(ctx);

            var all = await issuesQuery.ListAsync(project.Id, null, all: true);
            var completed = all.Where(i => i.Status == "done" && i.ArchivedAt == null).ToList();
            var skipped = all.Where(i => i.Status != "done" && i.ArchivedAt == null).ToList();
            var cleanupFailed = 0;

            foreach (var issue in completed)
            {
                var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
                try
                {
                    if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is not null)
                    {
                        cleanupFailed++;
                        continue;
                    }
                    var repoPath = issue.Repository!.Path ?? IssueRepositoryResolutionHelpers.DefaultRepoPath;
                    var projectName = issue.ProjectName ?? "project";
                    await grain.ArchiveAsync();
                    var cleanup = await git.RemoveWorktreeAsync(repoPath, projectName, issue.Number);
                    if (cleanup.Status == "failed") cleanupFailed++;
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
                cleanupFailed,
                message = $"Archived {completed.Count} completed issues, skipped {skipped.Count}"
            });
        });
    }
}
