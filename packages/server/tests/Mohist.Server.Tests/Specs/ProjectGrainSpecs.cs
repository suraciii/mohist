using Mohist.Server.Project.Grains;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ProjectGrainSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly IGrainFactory _grains;

    public ProjectGrainSpecs(WorkflowGrainFixture fixture)
    {
        _grains = fixture.Grains;
    }

    private IProjectGrain NewProjects() =>
        _grains.GetGrain<IProjectGrain>(Guid.NewGuid().ToString());

    [Fact]
    public async Task CreateProject_ReturnsProjectWithId()
    {
        var projects = NewProjects();
        var project = await projects.CreateAsync("my-app", "/home/user/my-app", null);

        Assert.NotNull(project);
        Assert.Equal("my-app", project.Name);
        Assert.Equal("/home/user/my-app", project.Path);
        Assert.Equal("main", project.BaseBranch);
        Assert.StartsWith("proj_", project.Id);
    }

    [Fact]
    public async Task CreateProject_DuplicateName_Throws()
    {
        var projects = NewProjects();
        await projects.CreateAsync("dup", "/a", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projects.CreateAsync("dup", "/b", null));
    }

    [Fact]
    public async Task GetByName_Existing_ReturnsProject()
    {
        var projects = NewProjects();
        await projects.CreateAsync("find-me", "/find", "develop");
        var project = await projects.GetByNameAsync("find-me");

        Assert.NotNull(project);
        Assert.Equal("find-me", project.Name);
        Assert.Equal("develop", project.BaseBranch);
    }

    [Fact]
    public async Task GetByName_NotExisting_ReturnsNull()
    {
        var projects = NewProjects();
        var project = await projects.GetByNameAsync("no-such");
        Assert.Null(project);
    }

    [Fact]
    public async Task GetAll_ReturnsAllProjects()
    {
        var projects = NewProjects();
        await projects.CreateAsync("p1", "/a", null);
        await projects.CreateAsync("p2", "/b", null);

        var all = await projects.GetAllAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Update_ChangesBaseBranch()
    {
        var projects = NewProjects();
        await projects.CreateAsync("updatable", "/up", "main");
        var updated = await projects.UpdateAsync("updatable", "develop");

        Assert.NotNull(updated);
        Assert.Equal("develop", updated!.BaseBranch);
    }

    [Fact]
    public async Task Update_NotExisting_ReturnsNull()
    {
        var projects = NewProjects();
        var result = await projects.UpdateAsync("ghost", "main");
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_Existing_RemovesProject()
    {
        var projects = NewProjects();
        await projects.CreateAsync("deletable", "/del", null);
        var deleted = await projects.DeleteAsync("deletable");

        Assert.True(deleted);
        Assert.Null(await projects.GetByNameAsync("deletable"));
    }

    [Fact]
    public async Task Delete_NotExisting_ReturnsFalse()
    {
        var projects = NewProjects();
        var deleted = await projects.DeleteAsync("ghost");
        Assert.False(deleted);
    }
}
