using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.OrleansTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Grains;
using Xunit;

namespace Mohist.Server.OrleansTests.Specs;

[Collection("WorkflowGrain")]
public class WorkspaceGrainSpecs
{
    private readonly OrleansL0WorkflowGrainFixture _fixture;
    private readonly string _projectId;

    public WorkspaceGrainSpecs(OrleansL0WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
        _projectId = fixture.SeedWorkspaceProject();
    }

    private IWorkspaceGrain Grain(string name) =>
        _fixture.Grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(_projectId, name));

    private string UniqueName(string purpose) => $"{purpose}-{Guid.NewGuid():N}";

    [Fact]
    public async Task CreateAsync_SlackOrigin_CreatesActiveWorkspaceWithSlackOrigin()
    {
        var name = UniqueName("slack");
        var origin = new WorkspaceOrigin.Slack("T1", "C123");

        var created = await Grain(name).CreateAsync(name, origin, [], _fixture.TimeProvider.GetUtcNow());

        Assert.Equal(name, created.Name);
        Assert.Equal(origin, created.Origin);
        Assert.Equal(WorkspaceStatus.Active, created.Status);
        Assert.Empty(created.RepositoryNames);
        Assert.NotNull(await Grain(name).GetAsync());
    }

    [Fact]
    public async Task CreateAsync_WebOrigin_CreatesActiveWorkspaceWithWebOrigin()
    {
        var name = UniqueName("web");
        var origin = new WorkspaceOrigin.Web("conv-9");

        var created = await Grain(name).CreateAsync(name, origin, [], _fixture.TimeProvider.GetUtcNow());

        Assert.Equal(origin, created.Origin);
        Assert.Equal(WorkspaceStatus.Active, created.Status);
    }

    [Fact]
    public async Task CreateAsync_SameActiveOriginUnderDifferentName_ThrowsOriginConflict()
    {
        var firstName = UniqueName("origin-a");
        var secondName = UniqueName("origin-b");
        var origin = new WorkspaceOrigin.Slack("T1", "C-shared");
        await Grain(firstName).CreateAsync(firstName, origin, [], _fixture.TimeProvider.GetUtcNow());

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => Grain(secondName).CreateAsync(secondName, origin, [], _fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_origin_conflict", ex.Code);
    }

    [Fact]
    public async Task ArchiveByOriginAsync_ArchivesWorkspaceAndIsIdempotent()
    {
        var name = UniqueName("archive-slack");
        var origin = new WorkspaceOrigin.Slack("T1", "C-archive");
        await Grain(name).CreateAsync(name, origin, [], _fixture.TimeProvider.GetUtcNow());

        await Grain(name).ArchiveByOriginAsync(origin, _fixture.TimeProvider.GetUtcNow());
        var archived = await Grain(name).GetAsync();
        Assert.Equal(WorkspaceStatus.Archived, archived!.Status);
        Assert.NotNull(archived.ArchivedAt);

        await Grain(name).ArchiveByOriginAsync(origin, _fixture.TimeProvider.GetUtcNow());
        Assert.Equal(WorkspaceStatus.Archived, (await Grain(name).GetAsync())!.Status);
    }

    [Fact]
    public async Task ArchiveByOriginAsync_WrongOrigin_ThrowsOriginMismatch()
    {
        var name = UniqueName("archive-mismatch");
        await Grain(name).CreateAsync(name, new WorkspaceOrigin.Web("conv-m"), [], _fixture.TimeProvider.GetUtcNow());

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => Grain(name).ArchiveByOriginAsync(new WorkspaceOrigin.Slack("T1", "C-other"), _fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_origin_mismatch", ex.Code);
    }

    [Fact]
    public async Task CreateManualAsync_ValidInput_CreatesActiveManualWorkspace()
    {
        var name = UniqueName("pay");
        var created = await Grain(name).CreateManualAsync(name, ["server", "web"], _fixture.TimeProvider.GetUtcNow());

        Assert.Equal(name, created.Name);
        Assert.IsType<WorkspaceOrigin.Manual>(created.Origin);
        Assert.Equal(["server", "web"], created.RepositoryNames);
        Assert.Equal(WorkspaceStatus.Active, created.Status);
        Assert.Null(created.Home);

        var loaded = await Grain(name).GetAsync();
        Assert.NotNull(loaded);
        Assert.Equal(name, loaded!.Name);
    }

    [Fact]
    public async Task CreateManualAsync_DuplicateName_ThrowsNameTaken()
    {
        var name = UniqueName("dup");
        await Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow());

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_name_taken", ex.Code);
    }

    [Fact]
    public async Task CreateManualAsync_SecondManualWhileFirstActive_ThrowsOriginConflict()
    {
        var firstName = UniqueName("first");
        var secondName = UniqueName("second");
        await Grain(firstName).CreateManualAsync(firstName, ["server"], _fixture.TimeProvider.GetUtcNow());

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => Grain(secondName).CreateManualAsync(secondName, ["server"], _fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_origin_conflict", ex.Code);
    }

    [Fact]
    public async Task AddAndRemoveRepository_MutatesMembers()
    {
        var name = UniqueName("members");
        await Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow());

        var added = await Grain(name).AddRepositoryAsync("web");
        Assert.Equal(["server", "web"], added!.RepositoryNames);

        var removed = await Grain(name).RemoveRepositoryAsync("web");
        Assert.Equal(["server"], removed!.RepositoryNames);
    }

    [Fact]
    public async Task CloseAsync_ArchivesAndRejectsRepeatedClose()
    {
        var name = UniqueName("close");
        await Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow());

        var closed = await Grain(name).CloseAsync(_fixture.TimeProvider.GetUtcNow());
        Assert.Equal(WorkspaceStatus.Archived, closed!.Status);
        Assert.NotNull(closed.ArchivedAt);

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => Grain(name).CloseAsync(_fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_already_archived", ex.Code);
    }

    [Fact]
    public async Task CloseAsync_ArchivedWorkspaceRejectsRepositoryChanges()
    {
        var name = UniqueName("close-then-add");
        await Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow());
        await Grain(name).CloseAsync(_fixture.TimeProvider.GetUtcNow());

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => Grain(name).AddRepositoryAsync("web"));
        Assert.Equal("workspace_archived", ex.Code);
    }

    [Fact]
    public async Task EnsureMaterializedOnAsync_FirstWriterWins()
    {
        var name = UniqueName("home");
        await Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow());

        var home = await Grain(name).EnsureMaterializedOnAsync("runner-a", $"/ws/{name}", _fixture.TimeProvider.GetUtcNow());
        Assert.Equal("runner-a", home!.RunnerId);

        var ex = await Assert.ThrowsAsync<WorkspaceDomainException>(
            () => Grain(name).EnsureMaterializedOnAsync("runner-b", $"/ws2/{name}", _fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("workspace_home_claimed", ex.Code);
    }

    [Fact]
    public async Task EnsureMaterializedOnAsync_SameRunnerRematerialization_UpdatesPath()
    {
        var name = UniqueName("home-remat");
        await Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow());

        await Grain(name).EnsureMaterializedOnAsync("runner-a", $"/ws/{name}", _fixture.TimeProvider.GetUtcNow());
        var home = await Grain(name).EnsureMaterializedOnAsync("runner-a", $"/ws/{name}-2", _fixture.TimeProvider.GetUtcNow());

        Assert.Equal($"/ws/{name}-2", home!.Path);
    }

    [Fact]
    public async Task ClearHomeIfAsync_OnlyClearsOwnHome()
    {
        var name = UniqueName("home-clear");
        await Grain(name).CreateManualAsync(name, ["server"], _fixture.TimeProvider.GetUtcNow());
        await Grain(name).EnsureMaterializedOnAsync("runner-a", $"/ws/{name}", _fixture.TimeProvider.GetUtcNow());

        await Grain(name).ClearHomeIfAsync("runner-b");
        Assert.NotNull(await Grain(name).GetHomeAsync());

        await Grain(name).ClearHomeIfAsync("runner-a");
        Assert.Null(await Grain(name).GetHomeAsync());
    }

}
