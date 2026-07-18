using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

/// <summary>
/// issue-417 T-003: cover the Issue aggregate's required-repository,
/// permanent start lock, repository reassignment, and reopen-target
/// behaviors. Acceptance criteria for these scenarios are spelled out
/// in
/// <c>openspec/changes/issue-417/specs/issue-repository-binding/spec.md</c>.
/// </summary>
public class IssueRepositoryBindingTests
{
    private const string ProjectId = "project-1";
    private const int Number = 1;

    [Fact]
    public void Create_WithoutRepository_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Mohist.Server.Issue.Domain.Issue.Create(
                ProjectId, Number, "Title",
                repositoryRef: null));
    }

    [Fact]
    public void Create_WithBlankRepository_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Mohist.Server.Issue.Domain.Issue.Create(
                ProjectId, Number, "Title",
                repositoryRef: "   "));
    }

    [Fact]
    public void Create_StoresRepository_AndInitialRevision()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");

        Assert.Equal("main", issue.RepositoryRef);
        Assert.Equal(1L, issue.RepositoryBindingRevision);
        Assert.Null(issue.LastRepositoryCommand);
        Assert.False(issue.HasWorkflowStarted);
    }

    [Fact]
    public void Create_WithCommand_RecordsReceipt()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title",
            repositoryRef: "main",
            commandId: "cmd-1");

        Assert.Equal("cmd-1", issue.LastRepositoryCommand?.CommandId);
        Assert.Equal("create", issue.LastRepositoryCommand?.Kind);
        Assert.Equal("main", issue.LastRepositoryCommand?.RepositoryName);
        Assert.Equal(1L, issue.LastRepositoryCommand?.AppliedRevision);
    }

    [Fact]
    public void State_RoundTripsRepositoryBindingState()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title",
            repositoryRef: "main", commandId: "cmd-1");

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal("main", reloaded!.RepositoryRef);
        Assert.True(reloaded.HasWorkflowStarted is false);
        Assert.Equal(1L, reloaded.RepositoryBindingRevision);
        Assert.Equal("cmd-1", reloaded.LastRepositoryCommand?.CommandId);
    }

    [Fact]
    public void StartWorkflow_SetsHasWorkflowStarted_Atomically()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");

        Assert.False(issue.HasWorkflowStarted);

        issue.StartWorkflow("wr_1");

        Assert.True(issue.HasWorkflowStarted);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
    }

    [Fact]
    public void Start_SetsHasWorkflowStarted_Atomically()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);

        issue.Start("wr_1", undeliveredPrerequisites: null);

        Assert.True(issue.HasWorkflowStarted);
        Assert.Equal("wr_1", issue.WorkflowRunId);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
    }

    [Theory]
    [InlineData(IssueStatus.Done)]
    [InlineData(IssueStatus.Cancelled)]
    public void HasWorkflowStarted_SurvivesTerminalState(IssueStatus terminalStatus)
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);
        Assert.True(issue.HasWorkflowStarted);

        if (terminalStatus == IssueStatus.Done)
            issue.Complete("wr_1");
        else
            issue.Close();

        Assert.Equal(terminalStatus, issue.Status);
        Assert.True(issue.HasWorkflowStarted);
    }

    [Fact]
    public void HasWorkflowStarted_SurvivesReopenFromCancelled()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);
        issue.Close();
        Assert.True(issue.HasWorkflowStarted);

        issue.Reopen(targetExists: true);

        Assert.Equal(IssueStatus.Backlog, issue.Status);
        Assert.True(issue.HasWorkflowStarted);
    }

    [Fact]
    public void HasWorkflowStarted_SurvivesClearStoppedWorkflow()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);
        Assert.True(issue.HasWorkflowStarted);

        issue.ClearStoppedWorkflow("wr_1");

        Assert.True(issue.HasWorkflowStarted);
        Assert.Null(issue.WorkflowRunId);
    }

    [Fact]
    public void ChangeRepository_OnUnstartedIssue_StoresCanonicalName()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");

        issue.ChangeRepository("web", "cmd-2", expectedRevision: 1L);

        Assert.Equal("web", issue.RepositoryRef);
        Assert.Equal(2L, issue.RepositoryBindingRevision);
        Assert.Equal("cmd-2", issue.LastRepositoryCommand?.CommandId);
        Assert.Equal("web", issue.LastRepositoryCommand?.RepositoryName);
    }

    [Fact]
    public void ChangeRepository_StaleRevision_Throws()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");

        Assert.Throws<IssueRepositoryStaleRevisionException>(() =>
            issue.ChangeRepository("web", "cmd-2", expectedRevision: 99L));
    }

    [Fact]
    public void ChangeRepository_EmptyName_Throws()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");

        Assert.Throws<ArgumentException>(() =>
            issue.ChangeRepository("  ", "cmd-2", expectedRevision: 1L));
    }

    [Fact]
    public void ChangeRepository_AfterStart_Rejects()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);

        Assert.Throws<IssueRepositoryLockedException>(() =>
            issue.ChangeRepository("web", "cmd-2", expectedRevision: 1L));
        Assert.Equal("main", issue.RepositoryRef);
    }

    [Fact]
    public void ChangeRepository_AfterCompletion_StillRejects()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);
        issue.Complete("wr_1");

        Assert.Throws<IssueRepositoryLockedException>(() =>
            issue.ChangeRepository("web", "cmd-2", expectedRevision: 1L));
    }

    [Fact]
    public void ChangeRepository_OnUnstartedClosedIssue_DoesNotFirePostStartLock()
    {
        // An Issue that was never started but operator-cancelled remains
        // eligible for reassignment. The post-start lock guard checks
        // HasWorkflowStarted, not status — so the absence of a started
        // run means the lock is not engaged. Reopen (to backlog) is what
        // makes the cancelled-to-backlog move; this test only covers the
        // lock-vs-no-lock check.
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");
        issue.Close();

        Assert.False(issue.HasWorkflowStarted);
        Assert.Equal(IssueStatus.Cancelled, issue.Status);

        var ex = Record.Exception(() =>
            issue.ChangeRepository("web", "cmd-2", expectedRevision: 1L));
        Assert.False(ex is IssueRepositoryLockedException,
            $"Post-start lock must not fire for an unstarted issue (got {ex?.GetType().Name})");
    }

    [Fact]
    public void Reopen_WhenTargetMissing_Throws()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);
        issue.Close();

        Assert.Throws<IssueRepositoryMissingOnReopenException>(() =>
            issue.Reopen(targetExists: false));

        Assert.Equal(IssueStatus.Cancelled, issue.Status);
    }

    [Fact]
    public void Reopen_WhenTargetExists_Succeeds()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);
        issue.Close();

        issue.Reopen(targetExists: true);

        Assert.Equal(IssueStatus.Backlog, issue.Status);
        Assert.True(issue.HasWorkflowStarted);
    }

    [Fact]
    public void RecordRepositoryCommandReceipt_OnNoOp_AdvancesRevisionWithoutChangingRepo()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");
        issue.ChangeRepository("web", "cmd-2", expectedRevision: 1L);

        issue.RecordRepositoryCommandReceipt("cmd-2b", "change", expectedRevision: 2L);

        Assert.Equal("web", issue.RepositoryRef);
        Assert.Equal(3L, issue.RepositoryBindingRevision);
        Assert.Equal("cmd-2b", issue.LastRepositoryCommand?.CommandId);
    }

    [Fact]
    public void ChangeRepository_EmitsIssueRepositoryChangedEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");

        issue.ChangeRepository("web", "cmd-2", expectedRevision: 1L);

        IssueRepositoryChanged? evt = null;
        foreach (var pending in issue.PendingEvents)
        {
            if (pending is IssueRepositoryChanged changed)
            {
                evt = changed;
                break;
            }
        }
        Assert.NotNull(evt);
        Assert.Equal("main", evt!.OldRepositoryRef);
        Assert.Equal("web", evt.NewRepositoryRef);
        Assert.Equal("cmd-2", evt.CommandId);
        Assert.Equal(1L, evt.ExpectedRevision);
        Assert.Equal(2L, evt.AppliedRevision);
    }

    [Fact]
    public void Create_WithoutCommand_DoesNotEmitIssueRepositoryChanged()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");

        var hasRepoEvent = false;
        var hasCreated = false;
        foreach (var pending in issue.PendingEvents)
        {
            if (pending is IssueRepositoryChanged) hasRepoEvent = true;
            if (pending is IssueCreated) hasCreated = true;
        }
        Assert.False(hasRepoEvent);
        Assert.True(hasCreated);
    }

    [Fact]
    public void State_RoundTripsChangeRepositoryEventPayload()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main");
        issue.ChangeRepository("web", "cmd-2", expectedRevision: 1L);

        var json = IssueStore.Serialize(issue);
        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal("web", reloaded!.RepositoryRef);
        Assert.Equal(2L, reloaded.RepositoryBindingRevision);
        Assert.Equal("cmd-2", reloaded.LastRepositoryCommand?.CommandId);
        Assert.True(reloaded.HasWorkflowStarted is false);
    }

    [Fact]
    public void Reopen_DoesNotClearHasWorkflowStarted()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            ProjectId, Number, "Title", repositoryRef: "main",
            isDraft: false);
        issue.Start("wr_1", undeliveredPrerequisites: null);
        issue.Close();
        Assert.True(issue.HasWorkflowStarted);

        issue.Reopen(targetExists: true);

        Assert.True(issue.HasWorkflowStarted);
    }
}