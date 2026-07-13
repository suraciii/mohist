using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Grain;

public class ProjectGrainSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly WorkflowGrainFixture _fixture;
    private readonly IGrainFactory _grains;

    public ProjectGrainSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
    }

    private IProjectGrain NewProjectGrain(string? id = null) =>
        _grains.GetGrain<IProjectGrain>(id ?? Guid.NewGuid().ToString());

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task CreateProject_DoesNotCreateDefaultRepository()
    {
        var grain = NewProjectGrain();
        var project = await grain.CreateAsync("no-default-repo");

        Assert.Empty(project.Repositories);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task CreateProject_Duplicate_Throws()
    {
        var id = Guid.NewGuid().ToString();
        var grain = _grains.GetGrain<IProjectGrain>(id);
        await grain.CreateAsync("dup");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync("dup"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetAsync_Existing_ReturnsProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("find-me");
        var project = await grain.GetAsync();

        Assert.NotNull(project);
        Assert.Equal("find-me", project.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetAsync_NotExisting_ReturnsNull()
    {
        var grain = NewProjectGrain();
        var project = await grain.GetAsync();
        Assert.Null(project);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task Update_Existing_UpdatesTimestamp()
    {
        var grain = NewProjectGrain();
        var created = await grain.CreateAsync("updatable");
        var before = created.UpdatedAt;
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var updated = await grain.UpdateAsync();

        Assert.NotNull(updated);
        Assert.True(string.CompareOrdinal(updated!.UpdatedAt, before) > 0);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task Update_NotExisting_ReturnsNull()
    {
        var grain = NewProjectGrain();
        var result = await grain.UpdateAsync();
        Assert.Null(result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task Delete_Existing_RemovesProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("deletable");
        await grain.DeleteAsync();

        Assert.Null(await grain.GetAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task AddRepository_AddsToProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("repo-test");
        var updated = await grain.AddRepositoryAsync("frontend", "git@example.com:frontend.git", "main");

        Assert.NotNull(updated);
        Assert.Single(updated!.Repositories);
        Assert.Contains(updated.Repositories, r => r.Name == "frontend");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task SetDefaultRepository_SwitchesDefault()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("default-test");
        await grain.AddRepositoryAsync("frontend", "git@example.com:frontend.git", "main");
        await grain.AddRepositoryAsync("backend", "git@example.com:backend.git", "main");
        await grain.SetDefaultRepositoryAsync("backend");

        var project = await grain.GetAsync();
        Assert.NotNull(project);
        var backend = project!.Repositories.First(r => r.Name == "backend");
        Assert.True(backend.IsDefault);
        var frontend = project.Repositories.First(r => r.Name == "frontend");
        Assert.False(frontend.IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task RemoveRepository_RemovesFromProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("remove-test");
        await grain.AddRepositoryAsync("temp", "git@example.com:temp.git", "main");
        var updated = await grain.RemoveRepositoryAsync("temp");

        Assert.NotNull(updated);
        Assert.Empty(updated!.Repositories);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task AddRepository_WithoutGitUrl_Throws()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("repo-no-url");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AddRepositoryAsync("backend", "", "main"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task UpdateRepository_ChangesGitUrlAndBaseBranch()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync("repo-update");
        await grain.AddRepositoryAsync("backend", "git@example.com:backend.git", "main");

        var updated = await grain.UpdateRepositoryAsync("backend", gitUrl: "git@example.com:backend-v2.git", baseBranch: "develop");

        Assert.NotNull(updated);
        var repo = updated!.Repositories.Single();
        Assert.Equal("git@example.com:backend-v2.git", repo.GitUrl);
        Assert.Equal("develop", repo.BaseBranch);
    }
}
