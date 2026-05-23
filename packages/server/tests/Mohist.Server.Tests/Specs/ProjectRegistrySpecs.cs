using Mohist.Server.Project.Grains;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ProjectRegistrySpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly IGrainFactory _grains;

    public ProjectRegistrySpecs(WorkflowGrainFixture fixture)
    {
        _grains = fixture.Grains;
    }

    private IProjectRegistryGrain NewRegistry() =>
        _grains.GetGrain<IProjectRegistryGrain>(Guid.NewGuid().ToString());

    [Fact]
    public async Task CreateProject_ReturnsProjectWithId()
    {
        var registry = NewRegistry();
        var project = await registry.CreateAsync("my-app", "/home/user/my-app", null);

        Assert.NotNull(project);
        Assert.Equal("my-app", project.Name);
        Assert.Equal("/home/user/my-app", project.Path);
        Assert.Equal("main", project.BaseBranch);
        Assert.StartsWith("proj_", project.Id);
    }

    [Fact]
    public async Task CreateProject_DuplicateName_Throws()
    {
        var registry = NewRegistry();
        await registry.CreateAsync("dup", "/a", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.CreateAsync("dup", "/b", null));
    }

    [Fact]
    public async Task GetByName_Existing_ReturnsProject()
    {
        var registry = NewRegistry();
        await registry.CreateAsync("find-me", "/find", "develop");
        var project = await registry.GetByNameAsync("find-me");

        Assert.NotNull(project);
        Assert.Equal("find-me", project.Name);
        Assert.Equal("develop", project.BaseBranch);
    }

    [Fact]
    public async Task GetByName_NotExisting_ReturnsNull()
    {
        var registry = NewRegistry();
        var project = await registry.GetByNameAsync("no-such");
        Assert.Null(project);
    }

    [Fact]
    public async Task GetAll_ReturnsAllProjects()
    {
        var registry = NewRegistry();
        await registry.CreateAsync("p1", "/a", null);
        await registry.CreateAsync("p2", "/b", null);

        var all = await registry.GetAllAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Update_ChangesBaseBranch()
    {
        var registry = NewRegistry();
        await registry.CreateAsync("updatable", "/up", "main");
        var updated = await registry.UpdateAsync("updatable", "develop");

        Assert.NotNull(updated);
        Assert.Equal("develop", updated!.BaseBranch);
    }

    [Fact]
    public async Task Update_NotExisting_ReturnsNull()
    {
        var registry = NewRegistry();
        var result = await registry.UpdateAsync("ghost", "main");
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_Existing_RemovesProject()
    {
        var registry = NewRegistry();
        await registry.CreateAsync("deletable", "/del", null);
        var deleted = await registry.DeleteAsync("deletable");

        Assert.True(deleted);
        Assert.Null(await registry.GetByNameAsync("deletable"));
    }

    [Fact]
    public async Task Delete_NotExisting_ReturnsFalse()
    {
        var registry = NewRegistry();
        var deleted = await registry.DeleteAsync("ghost");
        Assert.False(deleted);
    }

    [Fact]
    public async Task SetCurrent_Existing_SetsCurrentProject()
    {
        var registry = NewRegistry();
        await registry.CreateAsync("active", "/active", null);

        var set = await registry.SetCurrentAsync("active");
        Assert.NotNull(set);

        var current = await registry.GetCurrentAsync();
        Assert.Equal("active", current!.Name);
    }

    [Fact]
    public async Task SetCurrent_NotExisting_ReturnsNull()
    {
        var registry = NewRegistry();
        var result = await registry.SetCurrentAsync("ghost");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrent_NoCurrentSet_ReturnsNull()
    {
        var registry = NewRegistry();
        var current = await registry.GetCurrentAsync();
        Assert.Null(current);
    }

    [Fact]
    public async Task DeleteCurrent_ClearsCurrent()
    {
        var registry = NewRegistry();
        await registry.CreateAsync("temp", "/temp", null);
        await registry.SetCurrentAsync("temp");
        await registry.DeleteAsync("temp");

        var current = await registry.GetCurrentAsync();
        Assert.Null(current);
    }
}
