using System.Text.Json;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Api;

public sealed class RunnerPollParentContextMapperTests
{
    [Fact]
    public async Task WorkflowTaskMapsParentTitleAndBodyAtTheHttpBoundary()
    {
        var calls = 0;
        var response = await RunnerRoutes.ToWorkDispatchResponseAsync(
            Dispatch(),
            (projectId, issueNumber) =>
            {
                calls++;
                Assert.Equal(("proj-child", 42), (projectId, issueNumber));
                return Task.FromResult<ParentIssueContext?>(new("Parent title", "Parent body"));
            });

        Assert.Equal(1, calls);
        Assert.Equal(new ParentIssueContextResponse("Parent title", "Parent body"), response.ParentIssueContext);
        Assert.Equal(["Body", "Title"], typeof(ParentIssueContextResponse).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.DoesNotContain(typeof(WorkDispatch).GetProperties(), property => property.Name.Contains("ParentIssueContext", StringComparison.Ordinal));

        using var json = JsonDocument.Parse(JSON.Serialize(response));
        var context = json.RootElement.GetProperty("parentIssueContext");
        Assert.Equal(["body", "title"], context.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("Parent title", context.GetProperty("title").GetString());
        Assert.Equal("Parent body", context.GetProperty("body").GetString());
    }

    [Fact]
    public async Task OrdinaryPlanIssueMapsNoParentContext()
    {
        var calls = 0;
        var response = await RunnerRoutes.ToWorkDispatchResponseAsync(
            Dispatch(),
            (_, _) =>
            {
                calls++;
                return Task.FromResult<ParentIssueContext?>(null);
            });

        Assert.Equal(1, calls);
        Assert.Null(response.ParentIssueContext);
        using var json = JsonDocument.Parse(JSON.Serialize(response));
        Assert.False(json.RootElement.TryGetProperty("parentIssueContext", out _));
    }

    [Theory]
    [InlineData("build", "mohist/opencode")]
    [InlineData("check", "mohist/other")]
    [InlineData("integrate", "custom/action")]
    public async Task WorkflowTasksResolveParentContextRegardlessOfStageOrAction(string stage, string uses)
    {
        var calls = 0;
        var response = await RunnerRoutes.ToWorkDispatchResponseAsync(
            Dispatch(stage: stage, uses: uses),
            (projectId, issueNumber) =>
            {
                calls++;
                Assert.Equal(("proj-child", 42), (projectId, issueNumber));
                return Task.FromResult<ParentIssueContext?>(new("Parent title", "Parent body"));
            });

        Assert.Equal(1, calls);
        Assert.Equal(new ParentIssueContextResponse("Parent title", "Parent body"), response.ParentIssueContext);
    }

    [Fact]
    public async Task ChecksOtherActionsAgentJobsAndDetachedWorkDoNotResolveParentContext()
    {
        var dispatches = new[]
        {
            Dispatch(workType: WorkItemTypes.Checks),
            Dispatch(ownerKind: WorkDispatchOwnerKinds.AgentJob),
            Dispatch(includeIssue: false),
        };

        foreach (var dispatch in dispatches)
        {
            var response = await RunnerRoutes.ToWorkDispatchResponseAsync(dispatch, UnexpectedResolution);
            Assert.Null(response.ParentIssueContext);
        }
    }

    private static WorkDispatch Dispatch(
        string workType = WorkItemTypes.Task,
        string stage = "plan",
        string uses = "mohist/opencode",
        string ownerKind = WorkDispatchOwnerKinds.Workflow,
        bool includeIssue = true) =>
        new(
            "wr-parent-context",
            "plan.1",
            Uses: uses,
            With: "{\"prompt\":\"original\"}",
            WorkType: workType,
            Stage: stage,
            Issue: includeIssue ? new WorkIssueRef("proj-child", 42) : null,
            OwnerKind: ownerKind);

    private static Task<ParentIssueContext?> UnexpectedResolution(string projectId, int issueNumber) =>
        throw new Xunit.Sdk.XunitException($"Unexpected parent resolution for {projectId}#{issueNumber}");
}
