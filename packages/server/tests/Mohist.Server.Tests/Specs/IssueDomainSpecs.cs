using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class IssueDomainSpecs
{
    [Fact]
    public void StartWorkflow_MarksIssueInProgress()
    {
        var issue = Issue.Domain.Issue.Create(
            "issue_1",
            "project-1",
            1,
            "Build the feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));

        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Equal(IssueStatus.InProgress, issue.Status);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc), issue.UpdatedAt);
    }

    [Fact]
    public void Complete_IgnoresUnrelatedWorkflowRun()
    {
        var issue = Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");
        issue.StartWorkflow("wr_1");

        var completed = issue.Complete("wr_other");

        Assert.False(completed);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
    }

    [Fact]
    public void State_RoundTripsDomainState()
    {
        var issue = Issue.Domain.Issue.Create(
            "issue_1",
            "project-1",
            1,
            "Build the feature",
            labels: ["bug"],
            repositoryRef: "main",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.AddPrerequisite(42, new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 2, 0, DateTimeKind.Utc));

        var reloaded = IssueStore.Deserialize(IssueStore.Serialize(issue));

        Assert.NotNull(reloaded);
        Assert.Equal(issue.Id, reloaded!.Id);
        Assert.Equal(issue.ProjectId, reloaded.ProjectId);
        Assert.Equal(issue.Number, reloaded.Number);
        Assert.Equal(issue.Title, reloaded.Title);
        Assert.Equal(issue.Labels, reloaded.Labels);
        Assert.Equal(issue.RepositoryRef, reloaded.RepositoryRef);
        Assert.Equal(issue.PrerequisiteNumbers, reloaded.PrerequisiteNumbers);
        Assert.Equal(issue.WorkflowRunId, reloaded.WorkflowRunId);
        Assert.Equal(issue.Status, reloaded.Status);
    }
}
