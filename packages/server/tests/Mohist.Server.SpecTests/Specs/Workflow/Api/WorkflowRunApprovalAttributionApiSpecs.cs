using System.Net;
using System.Net.Http.Json;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Api;

public partial class WorkflowRunControlApiSpecs
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Approve_WithBlankAuthor_ApprovesWithoutAttribution(string author)
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/approve",
            new { author });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        var plan = run!.Stages.Single(s => s.Id == "plan");
        Assert.Equal(StageRunStatus.Completed, plan.Status);
        Assert.Null(plan.ApprovalStatus?.DecidedBy);
    }

    [Fact]
    public async Task Approve_WithoutAuthor_OnIssueRoute_ApprovesWithoutAttribution()
    {
        var (projectId, issueNumber, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/approve",
            new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        var plan = run!.Stages.Single(s => s.Id == "plan");
        Assert.Equal(StageRunStatus.Completed, plan.Status);
        Assert.Null(plan.ApprovalStatus?.DecidedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reject_WithBlankAuthor_RequestsChangesWithoutAttribution(string author)
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { author, message = "needs more detail" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Single(run!.Feedback);
        Assert.Null(run.Stages.Single(s => s.Id == "plan").ApprovalStatus?.DecidedBy);
    }

    [Fact]
    public async Task Reject_WithoutAuthor_RequestsChangesWithoutAttribution()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { message = "needs more detail" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Single(run!.Feedback);
        Assert.Null(run.Stages.Single(s => s.Id == "plan").ApprovalStatus?.DecidedBy);
    }
}
