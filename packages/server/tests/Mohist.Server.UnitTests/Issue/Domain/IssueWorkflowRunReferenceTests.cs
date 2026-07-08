using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

/// <summary>
/// Covers the rename from <c>ActiveWorkflowRunId</c> to a single
/// neutral <c>WorkflowRunId</c> and the rule that the reference
/// survives <c>Archive</c>/<c>Close</c> because it is an execution fact.
/// Spec: <c>openspec/changes/issue-264/specs/issue-workflow-run-reference/spec.md</c>.
/// </summary>
public class IssueWorkflowRunReferenceTests
{
    [Fact]
    public void Archive_PreservesWorkflowRunReference_AndSetsArchivedAt()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Done feature", isDraft: false);
        issue.StartWorkflow("wr_1");
        issue.Complete("wr_1");
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, issue.Status);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Null(issue.ArchivedAt);

        var archivedAt = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        issue.Archive(archivedAt);

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, issue.Status);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(archivedAt, issue.ArchivedAt);
        Assert.Equal(archivedAt, issue.UpdatedAt);
    }

    [Fact]
    public void Close_PreservesWorkflowRunReference_AndMarksCancelled()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Cancelable", isDraft: false);
        issue.StartWorkflow("wr_1");
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.InProgress, issue.Status);

        issue.Close("user-cancelled");

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Cancelled, issue.Status);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Null(issue.ArchivedAt);
    }

    [Fact]
    public void Unarchive_ClearsOnlyArchivedAt_DoesNotAlterWorkflowRunReference()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Archivable", isDraft: false);
        issue.StartWorkflow("wr_1");
        issue.Complete("wr_1");
        issue.Archive(new DateTime(2026, 6, 25, 11, 0, 0, DateTimeKind.Utc));
        Assert.NotNull(issue.ArchivedAt);
        Assert.Equal("wr_1", issue.WorkflowRunId);

        issue.Unarchive(new DateTime(2026, 6, 25, 13, 0, 0, DateTimeKind.Utc));

        Assert.Null(issue.ArchivedAt);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, issue.Status);
    }

    [Fact]
    public void Unarchive_OnAlreadyUnarchivedIssue_TouchesUpdatedAt_AndLeavesReference()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Not archived", isDraft: false);
        issue.StartWorkflow("wr_1");
        var before = issue.UpdatedAt;

        issue.Unarchive(new DateTime(2026, 6, 25, 14, 0, 0, DateTimeKind.Utc));

        Assert.Null(issue.ArchivedAt);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(new DateTime(2026, 6, 25, 14, 0, 0, DateTimeKind.Utc), issue.UpdatedAt);
        Assert.NotEqual(before, issue.UpdatedAt);
    }

    [Fact]
    public void State_RoundTripsArchivedDoneIssue_PreservingReference()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Round trip", isDraft: false);
        issue.StartWorkflow("wr_1");
        issue.Complete("wr_1");
        issue.Archive(new DateTime(2026, 6, 25, 9, 30, 0, DateTimeKind.Utc));

        var json = IssueStore.Serialize(issue);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("workflowRunId", out var wrId));
        Assert.Equal("wr_1", wrId.GetString());
        Assert.True(document.RootElement.TryGetProperty("archivedAt", out var archivedAtEl));
        Assert.False(document.RootElement.TryGetProperty("activeWorkflowRunId", out _));

        var reloaded = IssueStore.Deserialize(json);
        Assert.NotNull(reloaded);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, reloaded!.Status);
        Assert.Equal("wr_1", reloaded.WorkflowRunId);
        Assert.Equal(new DateTime(2026, 6, 25, 9, 30, 0, DateTimeKind.Utc), reloaded.ArchivedAt);
    }

    [Fact]
    public void State_RoundTripsCancelledIssue_PreservingReference()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Round trip cancel", isDraft: false);
        issue.StartWorkflow("wr_1");
        issue.Close("test");

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Cancelled, reloaded!.Status);
        Assert.Equal("wr_1", reloaded.WorkflowRunId);
    }

    [Fact]
    public void Domain_ExposesSingleWorkflowRunIdProperty_NoActiveAlias()
    {
        var propertyNames = typeof(Mohist.Server.Issue.Domain.Issue)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("WorkflowRunId", propertyNames);
        Assert.DoesNotContain("ActiveWorkflowRunId", propertyNames);

        var fieldNames = typeof(Mohist.Server.Issue.Domain.Issue)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("_workflowRunId", fieldNames);
        Assert.DoesNotContain("_activeWorkflowRunId", fieldNames);
    }

    [Fact]
    public void WorkflowProfileLockedException_ReferencesRunIdNotActiveWorkflow()
    {
        var withRun = new WorkflowProfileLockedException(7, "wr_1");
        Assert.Equal("7", withRun.IssueNumber);
        Assert.Equal("wr_1", withRun.WorkflowRunId);
        Assert.Null(withRun.GetType().GetProperty("ActiveWorkflowRunId"));
        Assert.Contains("workflow run reference", withRun.Message, StringComparison.OrdinalIgnoreCase);

        var withoutRun = new WorkflowProfileLockedException(7, null);
        Assert.Equal("7", withoutRun.IssueNumber);
        Assert.Null(withoutRun.WorkflowRunId);
        Assert.Contains("has started", withoutRun.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearStoppedWorkflow_OnlyResetsReferenceWhenIdMatches_AndLeavesArchiveAlone()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_1", "project-1", 1, "Cleared", isDraft: false);
        issue.StartWorkflow("wr_1");
        issue.Complete("wr_1");
        issue.Archive(new DateTime(2026, 6, 25, 8, 0, 0, DateTimeKind.Utc));
        var archivedAt = issue.ArchivedAt;

        issue.ClearStoppedWorkflow("wr_other");
        Assert.Equal("wr_1", issue.WorkflowRunId);

        issue.ClearStoppedWorkflow("wr_1");
        Assert.Null(issue.WorkflowRunId);
        Assert.Equal(archivedAt, issue.ArchivedAt);
    }
}
