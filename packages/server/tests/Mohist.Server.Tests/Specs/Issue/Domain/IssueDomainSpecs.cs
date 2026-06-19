using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using System.Text.Json;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Issue.Domain;

public class IssueDomainSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void StartWorkflow_MarksIssueInProgress()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1",
            "project-1",
            1,
            "Build the feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));

        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.InProgress, issue.Status);
        Assert.Equal("wr_1", issue.ActiveWorkflowRunId);
        Assert.Equal(new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc), issue.UpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Complete_IgnoresUnrelatedWorkflowRun()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");
        issue.StartWorkflow("wr_1");

        var completed = issue.Complete("wr_other");

        Assert.False(completed);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.InProgress, issue.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void State_RoundTripsDomainState()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1",
            "project-1",
            1,
            "Build the feature",
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "auth" },
            repositoryRef: "main",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.AddPrerequisite(42, new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 2, 0, DateTimeKind.Utc));

        var json = IssueStore.Serialize(issue);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("workflowRunId", out _));
        Assert.False(document.RootElement.TryGetProperty("activeWorkflowRunId", out _));

        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal(issue.Id, reloaded!.Id);
        Assert.Equal(issue.ProjectId, reloaded.ProjectId);
        Assert.Equal(issue.Number, reloaded.Number);
        Assert.Equal(issue.Title, reloaded.Title);
        Assert.Equal(issue.Labels, reloaded.Labels);
        Assert.Equal(issue.RepositoryRef, reloaded.RepositoryRef);
        Assert.Equal(issue.PrerequisiteNumbers, reloaded.PrerequisiteNumbers);
        Assert.Equal(issue.ActiveWorkflowRunId, reloaded.ActiveWorkflowRunId);
        Assert.Equal(issue.Status, reloaded.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Close_ClearsWorkflowReference_AndMarksCancelled()
    {
        // After the G3 fix, Close() clears ActiveWorkflowRunId so the issue
        // can be reopened or a new workflow started (the old "stuck issue"
        // pattern).
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");
        issue.StartWorkflow("wr_1");

        issue.Close();

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Cancelled, issue.Status);
        Assert.Null(issue.ActiveWorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Create_WithRisk_StoresValue()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Risked", risk: "high");

        Assert.Equal("high", issue.Risk);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Create_WithoutRisk_LeavesNull()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Plain");

        Assert.Null(issue.Risk);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Create_WithInvalidRisk_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Bad", risk: "extreme"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void State_RoundTripsRisk()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Risked", risk: "low");

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal("low", reloaded!.Risk);
    }
}
