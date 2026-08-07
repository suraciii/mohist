using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public class WorkspaceRepositoryDomainTests
{
    private static readonly WorkspaceRepositorySnapshot Snapshot =
        new("main", "https://example.test/mohist.git", "master");

    private static AgentSession CreateSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "proj")
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", "agent_1");
        var session = AgentSession.Create(
            "agent-session-1",
            "runner-1",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc));
        session.Settings = new AgentSessionSettings("opencode");
        return session;
    }

    [Fact]
    public void Initialize_RecordsUnconfirmedSource_AndIsIdempotent()
    {
        var session = CreateSession();

        session.InitializeWorkspaceRepository(Snapshot);
        var first = session.WorkspaceRepository;
        Assert.NotNull(first);
        Assert.Equal(WorkspaceRepositoryState.Unconfirmed, first!.State);
        Assert.Equal("main", first.Name);
        Assert.Equal("https://example.test/mohist.git", first.GitUrl);

        // A replay must not reset an already-decided source.
        session.InitializeWorkspaceRepository(new WorkspaceRepositorySnapshot("other", "url", "branch"));
        Assert.Same(first, session.WorkspaceRepository);
    }

    [Fact]
    public void Confirmation_AdvancesToConfirmed_WhenNameAndGitUrlMatch()
    {
        var session = CreateSession();
        session.InitializeWorkspaceRepository(Snapshot);

        var applied = session.ApplyWorkspaceSourceConfirmation("main", "https://example.test/mohist.git");

        Assert.True(applied);
        Assert.Equal(WorkspaceRepositoryState.Confirmed, session.WorkspaceRepository!.State);
        Assert.True(session.WorkspaceRepository.IsConfirmed);
    }

    [Theory]
    [InlineData("main", "https://example.test/other.git")] // gitUrl mismatch
    [InlineData("other", "https://example.test/mohist.git")] // name mismatch
    public void Confirmation_IsNoOp_WhenSnapshotDoesNotMatch(string name, string gitUrl)
    {
        var session = CreateSession();
        session.InitializeWorkspaceRepository(Snapshot);

        var applied = session.ApplyWorkspaceSourceConfirmation(name, gitUrl);

        Assert.False(applied);
        Assert.Equal(WorkspaceRepositoryState.Unconfirmed, session.WorkspaceRepository!.State);
    }

    [Fact]
    public void Confirmation_IsNoOp_WhenAlreadyDecided()
    {
        var session = CreateSession();
        session.InitializeWorkspaceRepository(Snapshot);
        Assert.True(session.ApplyWorkspaceSourceConfirmation("main", "https://example.test/mohist.git"));

        // A repeat report or a late mismatched report must not change the decided state.
        Assert.False(session.ApplyWorkspaceSourceConfirmation("main", "https://example.test/mohist.git"));
        Assert.False(session.ApplyWorkspaceSourceConfirmation("main", "https://example.test/other.git"));
        Assert.Equal(WorkspaceRepositoryState.Confirmed, session.WorkspaceRepository!.State);
    }

    [Fact]
    public void Rejection_IsTerminal_AndIdempotent()
    {
        var session = CreateSession();
        session.InitializeWorkspaceRepository(Snapshot);

        var applied = session.ApplyWorkspaceSourceRejection(
            "main",
            "https://example.test/mohist.git",
            WorkspaceSourceRejectionReason.OriginMismatch);

        Assert.True(applied);
        var rejected = session.WorkspaceRepository!;
        Assert.Equal(WorkspaceRepositoryState.Rejected, rejected.State);
        Assert.Equal(WorkspaceSourceRejectionReason.OriginMismatch, rejected.RejectionReason);

        // Rejected is terminal: a later confirmation report must not revive it.
        Assert.False(session.ApplyWorkspaceSourceConfirmation("main", "https://example.test/mohist.git"));
        Assert.Equal(WorkspaceRepositoryState.Rejected, session.WorkspaceRepository!.State);
    }

    [Fact]
    public void ReportsAreNoOp_WhenSessionHasNoProjectSource()
    {
        var session = CreateSession();

        Assert.False(session.ApplyWorkspaceSourceConfirmation("main", "https://example.test/mohist.git"));
        Assert.False(session.ApplyWorkspaceSourceRejection(
            "main", "https://example.test/mohist.git", WorkspaceSourceRejectionReason.NotRunnerOwned));
        Assert.Null(session.WorkspaceRepository);
    }
}
