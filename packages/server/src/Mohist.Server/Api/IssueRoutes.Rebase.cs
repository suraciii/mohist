using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

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
            IssueQuerier issuesQuery,
            WorkflowQuerier workflowQuerier) =>
        {
            var project = GetRequiredProject(ctx);
            var wrId = await ResolveWorkflowRunIdAsync(grains, issuesQuery, project.Id, number);
            if (wrId is null) return ApiResults.NotFound("No workflow run");

            var issue = await issuesQuery.GetAsync(project.Id, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");

            // Read the run-owned repository context instead of
            // composing live Project metadata. The run's authoritative
            // snapshot survives even if the Project's repository
            // declaration has since been removed (Cleanup after terminal).
            var runSnapshot = await workflowQuerier.GetRepositoryContextAsync(wrId);
            if (runSnapshot is null)
                return ApiResults.Conflict("Workflow run has no repository context; rebase not supported", "missing_repository_context");

            var workflow = grains.GetGrain<IWorkflowGrain>(wrId);
            if (await workflow.HasIncompleteTaskWithUsesAsync("mohist/rebase"))
                return ApiResults.Conflict("Rebase task is already pending", "rebase_already_pending");

            // Recovery for the API-injected rebase task is named in the
            // run's bound workflow profile; the C# route never authors
            // it. Resolution must come from the bound profile so the
            // task's `uses` and prompt reference track the workflow
            // content rather than a C# copy.
            var recovery = await workflowQuerier.GetRecoveryAsync(wrId, "rebase-conflicts");
            if (recovery is null)
                return ApiResults.Conflict(
                    "Bound workflow profile has no 'rebase-conflicts' recovery template",
                    "missing_rebase_recovery");

            // Omitted base uses the run snapshot; explicit base is
            // an operation-local override that must remain inside the
            // same verified repository.
            var baseBranch = !string.IsNullOrWhiteSpace(req?.BaseBranch)
                ? req!.BaseBranch!
                : runSnapshot.BaseBranch;
            var taskId = $"rebase-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var task = new RuntimeTaskInput(
                taskId,
                $"Rebase onto {baseBranch}",
                "mohist/rebase",
                BuildRebaseTaskWith(baseBranch),
                InvalidateChecks: true,
                Recovery: recovery);

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
