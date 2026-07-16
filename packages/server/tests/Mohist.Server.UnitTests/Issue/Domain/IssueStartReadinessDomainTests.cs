using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

public class IssueStartReadinessDomainTests
{
    [Fact]
    public void NewIssue_DefaultsToDraft()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");

        Assert.True(issue.IsDraft);
        Assert.IsType<IssueStartBlocker.Draft>(issue.StartBlocker(null));
        Assert.False(issue.CanStart(null));
    }

    [Fact]
    public void NewIssue_CanBeCreatedAsReady()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1", 1, "Build the feature", isDraft: false);

        Assert.False(issue.IsDraft);
        Assert.Null(issue.StartBlocker(null));
        Assert.True(issue.CanStart(null));
    }

    [Fact]
    public void SetDraft_MarksReadyOnDraftIssue_AndRecordsEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");
        Assert.True(issue.IsDraft);

        var now = new DateTime(2026, 6, 5, 1, 5, 0, DateTimeKind.Utc);
        issue.SetDraft(false, now);

        Assert.False(issue.IsDraft);
        Assert.Null(issue.StartBlocker(null));
        Assert.True(issue.CanStart(null));
        Assert.Equal(now, issue.UpdatedAt);

        var draftChanged = SingleDraftEvent(issue);
        Assert.True(draftChanged.OldIsDraft);
        Assert.False(draftChanged.NewIsDraft);
    }

    [Fact]
    public void SetDraft_MarksDraftOnReadyIssue_AndRecordsEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1", 1, "Build the feature", isDraft: false);
        Assert.False(issue.IsDraft);

        issue.SetDraft(true);

        Assert.True(issue.IsDraft);
        Assert.IsType<IssueStartBlocker.Draft>(issue.StartBlocker(null));
        Assert.False(issue.CanStart(null));

        var draftChanged = SingleDraftEvent(issue);
        Assert.False(draftChanged.OldIsDraft);
        Assert.True(draftChanged.NewIsDraft);
    }

    [Fact]
    public void SetDraft_SameValue_IsNoop()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");

        issue.SetDraft(true);

        Assert.True(issue.IsDraft);
        Assert.Equal(0, CountDraftEvents(issue));
    }

    private static int CountDraftEvents(Mohist.Server.Issue.Domain.Issue issue)
    {
        var count = 0;
        foreach (var evt in issue.PendingEvents)
        {
            if (evt is IssueDraftChanged) count++;
        }
        return count;
    }

    private static IssueDraftChanged SingleDraftEvent(Mohist.Server.Issue.Domain.Issue issue)
    {
        IssueDraftChanged? found = null;
        var count = 0;
        foreach (var evt in issue.PendingEvents)
        {
            if (evt is IssueDraftChanged d)
            {
                found = d;
                count++;
            }
        }
        Assert.NotNull(found);
        Assert.Equal(1, count);
        return found!;
    }

    [Fact]
    public void SetDraft_AfterStart_Throws()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature", isDraft: false);
        issue.Start("wr_1", null);

        Assert.Throws<InvalidOperationException>(() => issue.SetDraft(true));
    }

    [Fact]
    public void StartBlocker_DraftIssue_ReturnsDraft_RegardlessOfPrerequisites()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");
        issue.AddPrerequisite(42);

        var blocker = issue.StartBlocker(new HashSet<int> { 42 });

        Assert.IsType<IssueStartBlocker.Draft>(blocker);
    }

    [Fact]
    public void StartBlocker_ReadyIssueWaitingForPrereq_ReturnsWaitingFor_ForFirstUndelivered()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature", isDraft: false);
        issue.AddPrerequisite(7);
        issue.AddPrerequisite(9);

        var blocker = issue.StartBlocker(new HashSet<int> { 7, 9 });

        var waiting = Assert.IsType<IssueStartBlocker.WaitingFor>(blocker);
        Assert.Equal(7, waiting.PrerequisiteNumber);
    }

    [Fact]
    public void StartBlocker_ReadyIssue_AllPrereqsDelivered_ReturnsNull()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature", isDraft: false);
        issue.AddPrerequisite(7);

        Assert.Null(issue.StartBlocker(new HashSet<int>()));
        Assert.Null(issue.StartBlocker(null));
        Assert.True(issue.CanStart(new HashSet<int>()));
    }

    [Fact]
    public void StartBlocker_ReadyIssue_OnlyDeliveredPrereqs_ReturnsNull()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature", isDraft: false);
        issue.AddPrerequisite(7);
        issue.AddPrerequisite(9);

        var blocker = issue.StartBlocker(new HashSet<int>());

        Assert.Null(blocker);
    }

    [Fact]
    public void CanStart_DerivesFromStartBlocker_AndIsNeverAuthored()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature", isDraft: false);

        Assert.True(issue.CanStart(null));

        issue.AddPrerequisite(5);
        Assert.False(issue.CanStart(new HashSet<int> { 5 }));

        Assert.IsType<IssueStartBlocker.WaitingFor>(issue.StartBlocker(new HashSet<int> { 5 }));
    }

    [Fact]
    public void Start_OnDraftIssue_ThrowsBlockedException_WithDraftBlocker()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");

        var ex = Assert.Throws<IssueStartBlockedException>(() => issue.Start("wr_1", null));

        Assert.IsType<IssueStartBlocker.Draft>(ex.Blocker);
        Assert.Contains("still a draft", ex.Message);
    }

    [Fact]
    public void Start_OnReadyIssueWaitingForPrereq_ThrowsBlockedException_WithWaitingForBlocker()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature", isDraft: false);
        issue.AddPrerequisite(11);

        var ex = Assert.Throws<IssueStartBlockedException>(
            () => issue.Start("wr_1", new HashSet<int> { 11 }));

        var waiting = Assert.IsType<IssueStartBlocker.WaitingFor>(ex.Blocker);
        Assert.Equal(11, waiting.PrerequisiteNumber);
        Assert.Contains("waiting", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_OnReadyUnblockedIssue_EntersPipeline()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature", isDraft: false);
        var now = new DateTime(2026, 6, 5, 1, 10, 0, DateTimeKind.Utc);

        issue.Start("wr_1", null, now);

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.InProgress, issue.Status);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(now, issue.UpdatedAt);

        var hasWorkStarted = false;
        foreach (var evt in issue.PendingEvents)
        {
            if (evt is IssueWorkStarted) { hasWorkStarted = true; break; }
        }
        Assert.True(hasWorkStarted);
    }

    [Fact]
    public void Start_OnTerminalIssue_ThrowsInvalidOperation_AndDoesNotEnqueue()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature", isDraft: false);
        issue.Start("wr_1", null);
        issue.Complete("wr_1");

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, issue.Status);

        Assert.Throws<InvalidOperationException>(() => issue.Start("wr_2", null));
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Done, issue.Status);
    }

    [Fact]
    public void Start_OnAlreadyRunningIssue_ThrowsInvalidOperation()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature", isDraft: false);
        issue.Start("wr_1", null);

        Assert.Throws<InvalidOperationException>(() => issue.Start("wr_2", null));
        Assert.Equal("wr_1", issue.WorkflowRunId);
    }

    [Fact]
    public void State_RoundTripsIsDraftForNewIssue()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");

        var json = IssueStore.Serialize(issue);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("isDraft", out var isDraftEl));
        Assert.True(isDraftEl.GetBoolean());

        var reloaded = IssueStore.Deserialize(json);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsDraft);
    }

    [Fact]
    public void Deserialize_StateBlobWithoutIsDraft_DefaultsToReady()
    {
        var legacyJson = """
        {
          "id": "issue_legacy",
          "projectId": "project-1",
          "number": 42,
          "title": "Legacy issue",
          "body": null,
          "labels": {},
          "priority": "p2",
          "repositoryRef": null,
          "createdAt": "2026-01-01T00:00:00Z",
          "updatedAt": "2026-01-01T00:00:00Z",
          "archivedAt": null,
          "status": "backlog",
          "prerequisiteNumbers": []
        }
        """;

        var reloaded = IssueStore.Deserialize(legacyJson);

        Assert.NotNull(reloaded);
        Assert.False(reloaded!.IsDraft);
        Assert.True(reloaded.CanStart(null));
        Assert.Null(reloaded.StartBlocker(null));
    }

    [Fact]
    public void State_RoundTripsIsDraft_AfterSetDraft()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");
        issue.SetDraft(false);

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.False(reloaded!.IsDraft);
    }

    [Fact]
    public void IsDraft_OrthogonalToStatus_AndMarkingReadyDoesNotChangeStatus()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("issue_1", "project-1", 1, "Build the feature");
        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Backlog, issue.Status);

        issue.SetDraft(false);

        Assert.Equal(Mohist.Server.Issue.Domain.IssueStatus.Backlog, issue.Status);
    }
}
