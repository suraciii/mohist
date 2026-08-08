using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Domain;

/// <summary>
/// Pure-domain specs for <see cref="DomainIssue.ChangeRepository"/> and
/// <see cref="DomainIssue.RecordRepositoryCommandReceipt"/>. The grain's
/// <c>ApplyRepositoryChangeAsync</c> calls these once the project
/// repository declaration has been validated. The route contract
/// (400 unknown repository + 404 unknown issue + 200 success +
/// status code leakage) stays in <c>IssueRepositoryBindingApiSpecs</c>.
/// </summary>
public class IssueRepositoryBindingDomainTests
{
    [Fact]
    public void ChangeRepository_ValidName_BeforeWorkflowStarted_Persists()
    {
        var issue = NewIssue(initialRepository: "main");

        issue.ChangeRepository("secondary", commandId: "cmd-1", expectedRevision: null);

        Assert.Equal("secondary", issue.RepositoryRef);
    }

    [Fact]
    public void RecordRepositoryCommandReceipt_StoresReceipt()
    {
        var issue = NewIssue(initialRepository: "main");

        issue.RecordRepositoryCommandReceipt("cmd-1", kind: "change", expectedRevision: null);

        Assert.NotNull(issue.LastRepositoryCommand);
        Assert.Equal("cmd-1", issue.LastRepositoryCommand!.CommandId);
        Assert.Equal("change", issue.LastRepositoryCommand.Kind);
        Assert.Equal("main", issue.LastRepositoryCommand.RepositoryName);
    }

    [Fact]
    public void ChangeRepository_DifferentName_RecordsChangedEvent()
    {
        var issue = NewIssue(initialRepository: "main");

        issue.ChangeRepository("secondary", commandId: "cmd-1", expectedRevision: null);

        Assert.Contains(issue.PendingEvents, e => e is IssueRepositoryChanged);
        Assert.Equal("secondary", issue.RepositoryRef);
    }

    [Fact]
    public void ChangeRepository_AfterWorkflowStarted_Throws()
    {
        var issue = NewIssue(initialRepository: "main");
        issue.StartWorkflow("wr_seed");

        Assert.Throws<IssueRepositoryLockedException>(() =>
            issue.ChangeRepository("secondary", commandId: "cmd-1", expectedRevision: null));
    }

    [Fact]
    public void ChangeRepository_EmptyName_Throws()
    {
        var issue = NewIssue(initialRepository: "main");

        Assert.Throws<ArgumentException>(() =>
            issue.ChangeRepository("", commandId: "cmd-1", expectedRevision: null));
    }

    [Fact]
    public void ChangeRepository_StaleRevision_Throws()
    {
        var issue = NewIssue(initialRepository: "main");
        issue.ChangeRepository("secondary", commandId: "cmd-1", expectedRevision: null);

        // Now the stored revision has advanced; passing the original
        // null expectedRevision is fine, but a stale non-null throws.
        Assert.Throws<IssueRepositoryStaleRevisionException>(() =>
            issue.ChangeRepository("main", commandId: "cmd-2", expectedRevision: 0));
    }

    [Fact]
    public void RecordRepositoryCommandReceipt_ValidCall_AdvancesRevision()
    {
        var issue = NewIssue(initialRepository: "main");
        var before = issue.RepositoryBindingRevision;

        issue.RecordRepositoryCommandReceipt("cmd-1", kind: "change", expectedRevision: null);

        Assert.True(issue.RepositoryBindingRevision > before);
        Assert.NotNull(issue.LastRepositoryCommand);
        Assert.Equal("cmd-1", issue.LastRepositoryCommand!.CommandId);
        Assert.Equal("change", issue.LastRepositoryCommand.Kind);
    }

    private static DomainIssue NewIssue(string initialRepository)
    {
        return DomainIssue.Create(
            projectId: "proj-repo",
            number: 1,
            title: "Repo bind seed",
            repositoryRef: initialRepository,
            isDraft: false,
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
