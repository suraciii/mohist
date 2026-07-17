using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueRebase(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/rebase", async (
            HttpContext ctx,
            string projectRef,
            int number,
            RebaseRequest? req,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var wrId = await ResolveWorkflowRunIdAsync(grains, issuesQuery, project.Id, number);
            if (wrId is null) return ApiResults.NotFound("No workflow run");

            var issue = await issuesQuery.GetAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is { } repoError) return repoError;

            var workflow = grains.GetGrain<IWorkflowGrain>(wrId);
            if (await workflow.HasIncompleteTaskWithUsesAsync("mohist/rebase"))
                return ApiResults.Conflict("Rebase task is already pending", "rebase_already_pending");

            var baseBranch = !string.IsNullOrWhiteSpace(req?.BaseBranch)
                ? req!.BaseBranch!
                : (string.IsNullOrWhiteSpace(issue.Repository!.BaseBranch) ? IssueRepositoryResolutionHelpers.DefaultBaseBranch : issue.Repository.BaseBranch);
            var taskId = $"rebase-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var task = new RuntimeTaskInput(
                taskId,
                $"Rebase onto {baseBranch}",
                "mohist/rebase",
                BuildRebaseTaskWith(baseBranch, issue.Repository!),
                InvalidateChecks: true,
                Recovery: BuildRebaseRecovery());

            var added = await workflow.AddTaskAsync(task);
            return ApiResults.Ok(new
            {
                rebased = false,
                status = "queued",
                message = "Rebase task queued",
                workflowRunId = wrId,
                taskId = added.TaskId,
                stage = added.Stage,
                baseBranch,
            });
        });
    }
}
