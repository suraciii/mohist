using System.Text.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

internal static class WorkflowControlRecovery
{
    internal static bool IsWorkflowRunStateCorruption(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains("Failed to deserialize workflow run state", StringComparison.Ordinal))
                return true;

            if (current is JsonException)
                return true;
        }

        return false;
    }

    internal static async Task<IResult> RecoverIssueScopedRerunAsync(
        IGrainFactory grains,
        IssueQuerier issuesQuery,
        string projectId,
        int number)
    {
        var issueGrain = await IssueRoutes.GetIssueGrainAsync(grains, issuesQuery, projectId, number);
        if (issueGrain is null) return ApiResults.NotFound($"Issue #{number} not found");

        await issueGrain.StartWorkAsync();
        return ApiResults.Ok();
    }

    internal static async Task<IResult> RecoverWorkflowRunScopedRerunAsync(
        IGrainFactory grains,
        IssueQuerier issuesQuery,
        string workflowRunId)
    {
        var issue = await issuesQuery.GetIssueForWorkflowRunAsync(workflowRunId);
        if (issue is null)
            return ApiResults.NotFound($"Issue for workflow run '{workflowRunId}' not found");

        await grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(issue.ProjectId, issue.Number))).StartWorkAsync();
        return ApiResults.Ok();
    }
}
