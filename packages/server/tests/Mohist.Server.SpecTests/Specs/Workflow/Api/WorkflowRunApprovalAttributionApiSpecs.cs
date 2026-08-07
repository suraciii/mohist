using System.Net;
using System.Net.Http.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Api;

public partial class WorkflowRunControlApiSpecs
{
    /// <summary>
    /// #323 AC1: approval attribution comes from the authenticated
    /// principal, never from a self-declared request parameter. The spec
    /// fixture authenticates with the operator credential, which resolves
    /// to the service principal (id <c>service</c>), so every approval in
    /// this collection records <c>service</c> as decidedBy no matter what
    /// the request body claims.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Approve_WithBlankDisplayName_AttributesToAuthenticatedPrincipal(string displayName)
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/approve",
            new { displayName });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        var plan = run!.Stages.Single(s => s.Id == "plan");
        Assert.Equal(StageRunStatus.Completed, plan.Status);
        Assert.Equal("service", plan.ApprovalStatus?.DecidedBy);
        Assert.Null(plan.ApprovalStatus?.DisplayName);
    }

    [Fact]
    public async Task Approve_WithDisplayName_AttributesToPrincipalAndKeepsAliasForDisplay()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/approve",
            new { displayName = "supervisor" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        var plan = run!.Stages.Single(s => s.Id == "plan");
        Assert.Equal("service", plan.ApprovalStatus?.DecidedBy);
        Assert.Equal("supervisor", plan.ApprovalStatus?.DisplayName);
    }

    [Fact]
    public async Task Approve_WithoutDisplayName_OnIssueRoute_AttributesToAuthenticatedPrincipal()
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
        Assert.Equal("service", plan.ApprovalStatus?.DecidedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reject_WithBlankDisplayName_AttributesToAuthenticatedPrincipal(string displayName)
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { displayName, message = "needs more detail" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Single(run!.Feedback);
        Assert.Equal("service", run.Stages.Single(s => s.Id == "plan").ApprovalStatus?.DecidedBy);
    }

    [Fact]
    public async Task Reject_WithoutDisplayName_AttributesToAuthenticatedPrincipal()
    {
        var (_, _, _, wrId) = await SeedActiveWorkflowAsync();
        await ForceAwaitingApprovalAsync(wrId);

        var response = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{wrId}/reject",
            new { message = "needs more detail" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await LoadRunAsync(wrId);
        Assert.Single(run!.Feedback);
        Assert.Equal("service", run.Stages.Single(s => s.Id == "plan").ApprovalStatus?.DecidedBy);
    }
}
