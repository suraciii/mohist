using Mohist.Server.Issue.Domain;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Domain;

/// <summary>
/// Pure-domain specs for <see cref="DomainIssue"/>'s start-readiness
/// decision methods. The HTTP layer forwards the decision through
/// <see cref="DomainIssue.StartBlocker"/> and <see cref="DomainIssue.CanStart"/>;
/// the projection layer (<see cref="IssueStartReadinessProjectionSpecs"/>)
/// surfaces the result via the read-model. The route contract
/// (201/200/404/400 + JSON shape + 409 composite + prereq declaration)
/// stays in <c>IssueStartReadinessApiSpecs</c>.
/// </summary>
public class IssueStartReadinessDomainCoverageTests
{
    [Fact]
    public void StartBlocker_OnDraftIssue_ReturnsDraftBlocker()
    {
        var issue = NewIssue(isDraft: true);

        var blocker = issue.StartBlocker(undeliveredPrerequisites: null);

        var draft = Assert.IsType<IssueStartBlocker.Draft>(blocker);
        Assert.NotNull(draft);
    }

    [Fact]
    public void StartBlocker_OnReadyIssueWithNoPrerequisites_ReturnsNull()
    {
        var issue = NewIssue(isDraft: false);

        var blocker = issue.StartBlocker(undeliveredPrerequisites: null);

        Assert.Null(blocker);
    }

    [Fact]
    public void StartBlocker_OnReadyIssueWithWaitingPrerequisite_ReturnsWaitingFor()
    {
        var issue = NewIssue(isDraft: false);
        issue.AddPrerequisite(42);

        var blocker = issue.StartBlocker(new HashSet<int> { 42 });

        var waiting = Assert.IsType<IssueStartBlocker.WaitingFor>(blocker);
        Assert.Equal(42, waiting.PrerequisiteNumber);
    }

    [Fact]
    public void StartBlocker_OnReadyIssueWithAllPrerequisitesDelivered_ReturnsNull()
    {
        var issue = NewIssue(isDraft: false);
        issue.AddPrerequisite(42);
        issue.AddPrerequisite(7);

        var blocker = issue.StartBlocker(new HashSet<int>());

        Assert.Null(blocker);
    }

    [Fact]
    public void StartBlocker_OnReadyIssueWithUndeliveredPrereqNotListed_ReturnsNull()
    {
        var issue = NewIssue(isDraft: false);
        issue.AddPrerequisite(42);

        // The undelivered prereq 99 is not in this issue's prereq
        // list — the issue is only blocked by its own prereqs.
        var blocker = issue.StartBlocker(new HashSet<int> { 99 });

        Assert.Null(blocker);
    }

    [Fact]
    public void StartBlocker_OnDraftIssue_DraftTakesPrecedenceOverPrereq()
    {
        var issue = NewIssue(isDraft: true);
        issue.AddPrerequisite(42);

        var blocker = issue.StartBlocker(new HashSet<int> { 42 });

        Assert.IsType<IssueStartBlocker.Draft>(blocker);
    }

    [Fact]
    public void CanStart_OnReadyIssueWithNoPrereqs_True()
    {
        var issue = NewIssue(isDraft: false);

        Assert.True(issue.CanStart(undeliveredPrerequisites: null));
    }

    [Fact]
    public void CanStart_OnDraftIssue_False()
    {
        var issue = NewIssue(isDraft: true);

        Assert.False(issue.CanStart(undeliveredPrerequisites: null));
    }

    [Fact]
    public void CanStart_OnReadyIssueWithUndeliveredPrereq_False()
    {
        var issue = NewIssue(isDraft: false);
        issue.AddPrerequisite(42);

        Assert.False(issue.CanStart(new HashSet<int> { 42 }));
    }

    [Fact]
    public void Start_OnDraftIssue_Throws()
    {
        var issue = NewIssue(isDraft: true);

        Assert.Throws<IssueStartBlockedException>(() => issue.Start(
            wrId: "wr_seed",
            undeliveredPrerequisites: null));
    }

    [Fact]
    public void Start_OnReadyIssueWithUndeliveredPrereq_Throws()
    {
        var issue = NewIssue(isDraft: false);
        issue.AddPrerequisite(42);

        Assert.Throws<IssueStartBlockedException>(() => issue.Start(
            wrId: "wr_seed",
            undeliveredPrerequisites: new HashSet<int> { 42 }));
    }

    [Fact]
    public void Start_OnReadyUnblockedIssue_StartsAndRecordsWorkflow()
    {
        var issue = NewIssue(isDraft: false);

        issue.Start(wrId: "wr_seed", undeliveredPrerequisites: null);

        Assert.Equal("wr_seed", issue.WorkflowRunId);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
    }

    [Fact]
    public void SetDraft_TrueThenFalse_DraftBlockerGoesAway()
    {
        var issue = NewIssue(isDraft: true);
        Assert.IsType<IssueStartBlocker.Draft>(issue.StartBlocker(null));

        issue.SetDraft(false);

        Assert.Null(issue.StartBlocker(null));
        Assert.True(issue.CanStart(undeliveredPrerequisites: null));
    }

    private static DomainIssue NewIssue(bool isDraft)
    {
        return DomainIssue.Create(
            projectId: "proj-readiness",
            number: 1,
            title: "Readiness seed",
            repositoryRef: "main",
            isDraft: isDraft,
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
