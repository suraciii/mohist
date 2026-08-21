using Mohist.Server.Workspace.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Workspace;

public class WorkspaceStateTests
{
    private static WorkspaceState Active(WorkspaceOrigin? origin = null) => new()
    {
        ProjectId = "proj-1",
        Name = "pay",
        Origin = origin ?? new WorkspaceOrigin.Manual(),
        RepositoryNames = ["server"],
        Status = WorkspaceStatus.Active,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void RepositoryMembership_NormalizesAddAndRemovesCaseInsensitively()
    {
        var state = Active();

        state.AddRepository(" web ");
        state.RemoveRepository("WEB");

        Assert.Equal(["server"], state.RepositoryNames);
    }

    [Fact]
    public void ArchiveByOrigin_MatchingOriginArchivesOnce()
    {
        var origin = new WorkspaceOrigin.Slack("T1", "C1");
        var state = Active(origin);
        var archivedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);

        Assert.True(state.ArchiveByOrigin(origin, archivedAt));
        Assert.False(state.ArchiveByOrigin(origin, archivedAt.AddMinutes(1)));
        Assert.Equal(WorkspaceStatus.Archived, state.Status);
        Assert.Equal(archivedAt, state.ArchivedAt);
    }

    [Fact]
    public void ArchiveByOrigin_DifferentOriginReportsMismatch()
    {
        var state = Active(new WorkspaceOrigin.Web("conversation-1"));

        var error = Assert.Throws<WorkspaceDomainException>(() =>
            state.ArchiveByOrigin(new WorkspaceOrigin.Slack("T1", "C1"), DateTimeOffset.UnixEpoch));

        Assert.Equal("workspace_origin_mismatch", error.Code);
    }

    [Fact]
    public void Close_ManualWorkspaceArchivesAndRejectsSecondClose()
    {
        var state = Active();

        state.Close(DateTimeOffset.UnixEpoch.AddMinutes(1));
        var error = Assert.Throws<WorkspaceDomainException>(() => state.EnsureCloseAllowed());

        Assert.Equal(WorkspaceStatus.Archived, state.Status);
        Assert.Equal("workspace_already_archived", error.Code);
    }

    [Fact]
    public void Close_IssueWorkspaceReportsLifecycleOwnership()
    {
        var state = Active(new WorkspaceOrigin.Issue(42));

        var error = Assert.Throws<WorkspaceDomainException>(() => state.EnsureCloseAllowed());

        Assert.Equal("workspace_close_not_allowed_for_issue", error.Code);
    }

    [Fact]
    public void Materialize_SameRunnerCanMoveHomeButAnotherRunnerCannotClaimIt()
    {
        var state = Active();

        var first = state.EnsureMaterializedOn("runner-a", "/workspace/pay");
        Assert.Same(first, state.EnsureMaterializedOn("runner-a", "/workspace/pay"));
        Assert.Equal("/workspace/pay-2", state.EnsureMaterializedOn("runner-a", "/workspace/pay-2").Path);

        var error = Assert.Throws<WorkspaceDomainException>(() =>
            state.EnsureMaterializedOn("runner-b", "/workspace/pay"));
        Assert.Equal("workspace_home_claimed", error.Code);
    }

    [Fact]
    public void Materialize_ArchivedWorkspaceReportsArchived()
    {
        var state = Active();
        state.Close(DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<WorkspaceDomainException>(() =>
            state.EnsureMaterializedOn("runner-a", "/workspace/pay"));

        Assert.Equal("workspace_archived", error.Code);
    }

    [Fact]
    public void ClearHomeIf_OnlyOwnerClearsActiveHome()
    {
        var state = Active();
        var home = state.EnsureMaterializedOn("runner-a", "/workspace/pay");

        Assert.False(state.ClearHomeIf("runner-b"));
        Assert.Same(home, state.ActiveHome());
        Assert.True(state.ClearHomeIf("runner-a"));
        Assert.Null(state.ActiveHome());
    }

    [Fact]
    public void ActiveHome_ArchivedWorkspaceHidesHome()
    {
        var state = Active();
        state.EnsureMaterializedOn("runner-a", "/workspace/pay");
        state.Close(DateTimeOffset.UnixEpoch);

        Assert.Null(state.ActiveHome());
        var error = Assert.Throws<WorkspaceDomainException>(() => state.EnsureActive());
        Assert.Equal("workspace_archived", error.Code);
    }
}
