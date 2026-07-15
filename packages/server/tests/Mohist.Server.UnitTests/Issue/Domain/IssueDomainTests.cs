using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

public class IssueDomainTests
{
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
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc), issue.UpdatedAt);
    }

    [Fact]
    public void Complete_IgnoresUnrelatedWorkflowRun()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");
        issue.StartWorkflow("wr_1");

        var completed = issue.Complete("wr_other");

        Assert.False(completed);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.InProgress, issue.Status);
    }

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
        Assert.Equal(issue.WorkflowRunId, reloaded.WorkflowRunId);
        Assert.Equal(issue.Status, reloaded.Status);
    }

    [Fact]
    public void Close_PreservesWorkflowReference_AndMarksCancelled()
    {
        // The workflow run reference is an execution fact. Closing/cancelling
        // an issue records the cancelled state but must not sever the link to
        // the run that was bound to it. Reopen uses Issue.Reopen which
        // preserves the reference too; starting a new workflow requires an
        // explicit ClearStoppedWorkflow call on the grain (TryReuse path).
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");
        issue.StartWorkflow("wr_1");

        issue.Close();

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Cancelled, issue.Status);
        Assert.Equal("wr_1", issue.WorkflowRunId);
    }

    [Fact]
    public void Create_WithRisk_StoresValue()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Risked", risk: "high");

        Assert.Equal("high", issue.Risk);
    }

    [Fact]
    public void Create_WithoutRisk_LeavesNull()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Plain");

        Assert.Null(issue.Risk);
    }

    [Fact]
    public void Create_WithInvalidRisk_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Bad", risk: "extreme"));
    }

    [Fact]
    public void State_RoundTripsRisk()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Risked", risk: "low");

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal("low", reloaded!.Risk);
    }

    [Fact]
    public void NonTerminal_Issue_HasNullCompletedAt()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Backlog item",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));

        Assert.Null(issue.CompletedAt);

        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));
        Assert.Null(issue.CompletedAt);
    }

    [Fact]
    public void Complete_SetsCompletedAt_WhenEnteringDone()
    {
        var now = new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 30, 0, DateTimeKind.Utc));

        issue.Complete("wr_1", now);

        Assert.Equal(now, issue.CompletedAt);
        Assert.Equal(now, issue.UpdatedAt);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, issue.Status);
    }

    [Fact]
    public void Close_SetsCompletedAt_WhenEnteringCancelled()
    {
        var now = new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 30, 0, DateTimeKind.Utc));

        issue.Close(now: now);

        Assert.Equal(now, issue.CompletedAt);
        Assert.Equal(now, issue.UpdatedAt);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Cancelled, issue.Status);
    }

    [Fact]
    public void Reopen_PreservesCompletedAt()
    {
        var terminalMoment = new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 30, 0, DateTimeKind.Utc));
        issue.Close(now: terminalMoment);

        issue.Reopen(new DateTime(2026, 6, 5, 3, 0, 0, DateTimeKind.Utc));

        Assert.Equal(terminalMoment, issue.CompletedAt);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Backlog, issue.Status);
    }

    [Fact]
    public void Recomplete_AfterReopen_OverwritesCompletedAt()
    {
        var firstComplete = new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        var secondComplete = new DateTime(2026, 6, 6, 2, 0, 0, DateTimeKind.Utc);
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 30, 0, DateTimeKind.Utc));
        issue.Close(now: firstComplete);

        issue.Reopen(new DateTime(2026, 6, 5, 3, 0, 0, DateTimeKind.Utc));
        issue.ClearStoppedWorkflow("wr_1", new DateTime(2026, 6, 5, 3, 5, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_2", new DateTime(2026, 6, 5, 3, 10, 0, DateTimeKind.Utc));
        issue.Complete("wr_2", secondComplete);

        Assert.Equal(secondComplete, issue.CompletedAt);
        Assert.NotEqual(firstComplete, issue.CompletedAt);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, issue.Status);
    }

    [Fact]
    public void State_RoundTripsCompletedAt()
    {
        var now = new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 30, 0, DateTimeKind.Utc));
        issue.Complete("wr_1", now);

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal(now, reloaded!.CompletedAt);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, reloaded.Status);
    }

    [Fact]
    public void SetEpicId_StoresValueAndTouchesUpdatedAt_AndDoesNotEmitDomainEvent()
    {
        // SetEpicId is the eventless denormalized projection of epic
        // affiliation (D5). The authoritative EpicIssueLinked /
        // EpicIssueUnlinked events live on the epic stream — the
        // issue aggregate records no domain event for this transition.
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_1", new DateTime(2026, 6, 5, 1, 30, 0, DateTimeKind.Utc));
        var beforeTouch = issue.UpdatedAt;
        var pendingBefore = issue.PendingEvents.Count;

        issue.SetEpicId("epic_42", new DateTime(2026, 6, 5, 1, 45, 0, DateTimeKind.Utc));

        Assert.Equal("epic_42", issue.EpicId);
        Assert.Equal(new DateTime(2026, 6, 5, 1, 45, 0, DateTimeKind.Utc), issue.UpdatedAt);
        Assert.True(issue.UpdatedAt > beforeTouch);
        Assert.Equal(pendingBefore, issue.PendingEvents.Count);
    }

    [Fact]
    public void SetEpicId_NullClearsAffiliation()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Feature");
        issue.SetEpicId("epic_42");

        Assert.Equal("epic_42", issue.EpicId);

        issue.SetEpicId(null);

        Assert.Null(issue.EpicId);
    }

    [Fact]
    public void SetEpicId_NoOpWhenValueUnchanged()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Feature");
        issue.SetEpicId("epic_42", new DateTime(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc));
        var beforeTouch = issue.UpdatedAt;

        issue.SetEpicId("epic_42", new DateTime(2026, 6, 5, 3, 0, 0, DateTimeKind.Utc));

        Assert.Equal(beforeTouch, issue.UpdatedAt);
    }

    [Fact]
    public void SetEpicId_WhitespaceIsNormalizedToNull()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Feature");

        issue.SetEpicId("   ");

        Assert.Null(issue.EpicId);
    }

    [Fact]
    public void State_RoundTripsEpicId()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Feature");
        issue.SetEpicId("epic_42");

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal("epic_42", reloaded!.EpicId);
    }
}
