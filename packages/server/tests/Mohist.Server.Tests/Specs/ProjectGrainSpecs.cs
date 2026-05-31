using Mohist.Server.Project.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ProjectGrainSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly IGrainFactory _grains;

    public ProjectGrainSpecs(WorkflowGrainFixture fixture)
    {
        _grains = fixture.Grains;
    }

    private IProjectGrain NewProjectGrain(string? id = null) =>
        _grains.GetGrain<IProjectGrain>(id ?? Guid.NewGuid().ToString());

    [Fact]
    public async Task CreateProject_ReturnsProjectWithId()
    {
        var grain = NewProjectGrain();
        var project = await grain.CreateAsync("my-app", "/home/user/my-app", null);

        Assert.NotNull(project);
        Assert.Equal("my-app", project.Name);
        Assert.Equal("/home/user/my-app", project.Path);
        Assert.Equal("main", project.BaseBranch);
    }

    [Fact]
    public async Task CreateProject_Duplicate_Throws()
    {
        var id = Guid.NewGuid().ToString();
        var grain = _grains.GetGrain<IProjectGrain>(id);
        await grain.CreateAsync("dup", "/a", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync("dup", "/b", null));
    }

    [Fact]
    public async Task GetAsync_Existing_ReturnsProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("find-me", "/find", "develop");
        var project = await grain.GetAsync();

        Assert.NotNull(project);
        Assert.Equal("find-me", project.Name);
        Assert.Equal("develop", project.BaseBranch);
    }

    [Fact]
    public async Task GetAsync_NotExisting_ReturnsNull()
    {
        var grain = NewProjectGrain();
        var project = await grain.GetAsync();
        Assert.Null(project);
    }

    [Fact]
    public async Task Update_ChangesBaseBranch()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("updatable", "/up", "main");
        var updated = await grain.UpdateAsync("develop");

        Assert.NotNull(updated);
        Assert.Equal("develop", updated!.BaseBranch);
    }

    [Fact]
    public async Task Update_NotExisting_ReturnsNull()
    {
        var grain = NewProjectGrain();
        var result = await grain.UpdateAsync("main");
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_Existing_RemovesProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("deletable", "/del", null);
        await grain.DeleteAsync();

        Assert.Null(await grain.GetAsync());
    }

    [Fact]
    public async Task AddRepository_AddsToProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("repo-test", "/repo", null);
        var updated = await grain.AddRepositoryAsync("frontend", "/frontend", null, "main");

        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Repositories.Count);
        Assert.Contains(updated.Repositories, r => r.Name == "frontend");
    }

    [Fact]
    public async Task SetDefaultRepository_SwitchesDefault()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("default-test", "/default", null);
        await grain.AddRepositoryAsync("backend", "/backend", null, "main");
        await grain.SetDefaultRepositoryAsync("backend");

        var project = await grain.GetAsync();
        Assert.NotNull(project);
        var backend = project!.Repositories.First(r => r.Name == "backend");
        Assert.True(backend.IsDefault);
        var main = project.Repositories.First(r => r.Name == "main");
        Assert.False(main.IsDefault);
    }

    [Fact]
    public async Task RemoveRepository_RemovesFromProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("remove-test", "/remove", null);
        await grain.AddRepositoryAsync("temp", "/temp", null, "main");
        var updated = await grain.RemoveRepositoryAsync("temp");

        Assert.NotNull(updated);
        Assert.Single(updated!.Repositories);
    }
}
