using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Orleans.Core.Internal;
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

    [Fact]
    public async Task CreateProject_RequiresInitialRepository()
    {
        var grain = NewProjectGrain();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.CreateAsync("no-initial-repo", new RepositoryInfo
            {
                Name = string.Empty,
                GitUrl = "git@example.com:r.git",
                BaseBranch = "main",
                IsDefault = true,
            }));
    }

    [Fact]
    public async Task CreateProject_RequiresInitialRepositoryGitUrl()
    {
        var grain = NewProjectGrain();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.CreateAsync("no-giturl", new RepositoryInfo
            {
                Name = "main",
                GitUrl = string.Empty,
                BaseBranch = "main",
                IsDefault = true,
            }));
    }

    [Fact]
    public async Task CreateProject_WithInitialRepository_StoresSingleDefault()
    {
        var grain = NewProjectGrain();
        var project = await grain.CreateAsync(
            "single-repo",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        Assert.Single(project.Repositories);
        var repo = project.Repositories[0];
        Assert.Equal("main", repo.Name);
        Assert.Equal("git@example.com:main.git", repo.GitUrl);
        Assert.True(repo.IsDefault);
    }

    [Fact]
    public async Task CreateProject_Duplicate_Throws()
    {
        var id = Guid.NewGuid().ToString();
        var grain = _grains.GetGrain<IProjectGrain>(id);
        await grain.CreateAsync(
            "dup",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync(
                "dup",
                new RepositoryInfo
                {
                    Name = "main",
                    GitUrl = "git@example.com:main.git",
                    BaseBranch = "main",
                    IsDefault = true,
                }));
    }

    [Fact]
    public async Task GetAsync_Existing_ReturnsProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "find-me",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        var project = await grain.GetAsync();

        Assert.NotNull(project);
        Assert.Equal("find-me", project.Name);
    }

    [Fact]
    public async Task GetAsync_NotExisting_ReturnsNull()
    {
        var grain = NewProjectGrain();
        var project = await grain.GetAsync();
        Assert.Null(project);
    }

    [Fact]
    public async Task Update_Existing_UpdatesTimestamp()
    {
        var grain = NewProjectGrain();
        var created = await grain.CreateAsync(
            "updatable",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        var before = created.UpdatedAt;
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var updated = await grain.UpdateAsync();

        Assert.NotNull(updated);
        Assert.True(string.CompareOrdinal(updated!.UpdatedAt, before) > 0);
    }

    [Fact]
    public async Task Update_NotExisting_ReturnsNull()
    {
        var grain = NewProjectGrain();
        var result = await grain.UpdateAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_Existing_RemovesProject()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "deletable",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.DeleteAsync();

        Assert.Null(await grain.GetAsync());
    }

    [Fact]
    public async Task AddRepository_PreservesDefaultWhenNotSetDefault()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "switch-default-test",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.AddRepositoryAsync("frontend", "git@example.com:frontend.git", "main");
        await grain.AddRepositoryAsync("backend", "git@example.com:backend.git", "main");

        var project = await grain.GetAsync();
        Assert.NotNull(project);
        var server = project!.Repositories.Single(r => r.Name == "server");
        Assert.True(server.IsDefault);
        var frontend = project.Repositories.Single(r => r.Name == "frontend");
        Assert.False(frontend.IsDefault);
        var backend = project.Repositories.Single(r => r.Name == "backend");
        Assert.False(backend.IsDefault);
    }

    [Fact]
    public async Task AddRepository_WithSetDefault_RebindsDefault()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "set-default-on-add",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.AddRepositoryAsync("frontend", "git@example.com:frontend.git", "main", setDefault: true);

        var project = await grain.GetAsync();
        Assert.NotNull(project);
        var server = project!.Repositories.Single(r => r.Name == "server");
        Assert.False(server.IsDefault);
        var frontend = project.Repositories.Single(r => r.Name == "frontend");
        Assert.True(frontend.IsDefault);
    }

    [Fact]
    public async Task AddRepository_DuplicateNameDifferentCase_Rejected()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "dup-name",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AddRepositoryAsync("SERVER", "git@example.com:server-other.git", "main"));
    }

    [Fact]
    public async Task AddRepository_BlankGitUrl_Rejected()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "blank-giturl",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AddRepositoryAsync("server", "", "main"));
    }

    [Fact]
    public async Task AddRepository_BlankName_Rejected()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "blank-name",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.AddRepositoryAsync("", "git@example.com:r.git", "main"));
    }

    [Fact]
    public async Task AddRepository_BlankBaseBranch_DefaultsToMain()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "blank-basebranch",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@example.com:main.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        var updated = await grain.AddRepositoryAsync("web", "git@example.com:web.git", null);

        Assert.NotNull(updated);
        var web = updated!.Repositories.Single(r => r.Name == "web");
        Assert.Equal("main", web.BaseBranch);
    }

    [Fact]
    public async Task AddRepository_WhenPersistenceFails_LeavesActivatedAndReactivatedProjectUnchanged()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var grain = NewProjectGrain(id);
        await grain.CreateAsync(
            "persist-failure",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        var triggerName = $"fail_project_update_{Guid.NewGuid():N}";
        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var createTrigger = connection.CreateCommand();
        createTrigger.CommandText = $"""
            CREATE TRIGGER {triggerName}
            BEFORE UPDATE ON Projects
            WHEN NEW.Id = '{id}'
            BEGIN
                SELECT RAISE(ABORT, 'injected repository persistence failure');
            END;
            """;
        await createTrigger.ExecuteNonQueryAsync();

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                grain.AddRepositoryAsync("web", "git@example.com:web.git", "main"));

            var active = await grain.GetAsync();
            Assert.Single(active!.Repositories);
            Assert.Equal("server", active.Repositories[0].Name);
        }
        finally
        {
            await using var dropTrigger = connection.CreateCommand();
            dropTrigger.CommandText = $"DROP TRIGGER IF EXISTS {triggerName}";
            await dropTrigger.ExecuteNonQueryAsync();
        }

        var retried = await grain.AddRepositoryAsync("web", "git@example.com:web.git", "main");
        Assert.Equal(2, retried!.Repositories.Count);
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = await NewProjectGrain(id).GetAsync();
        Assert.Equal(2, reactivated!.Repositories.Count);
    }

    [Fact]
    public async Task AddRepository_WhenBackingRowIsMissing_LeavesActivatedProjectUnchanged()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var grain = NewProjectGrain(id);
        await grain.CreateAsync(
            "persist-before-publish",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        await using (var connection = new SqliteConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var db = new MohistDbContext(
                new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(connection).Options);
            db.Projects.Remove(await db.Projects.SingleAsync(project => project.Id == id));
            await db.SaveChangesAsync();
        }

        var result = await grain.AddRepositoryAsync("web", "git@example.com:web.git", "main");

        Assert.Null(result);
        var project = await grain.GetAsync();
        var repository = Assert.Single(project!.Repositories);
        Assert.Equal("server", repository.Name);
        Assert.True(repository.IsDefault);
    }

    [Fact]
    public async Task SetDefaultRepository_SwitchesDefault()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "default-test",
            new RepositoryInfo
            {
                Name = "frontend",
                GitUrl = "git@example.com:frontend.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.AddRepositoryAsync("backend", "git@example.com:backend.git", "main");
        await grain.SetDefaultRepositoryAsync("backend");

        var project = await grain.GetAsync();
        Assert.NotNull(project);
        var backend = project!.Repositories.First(r => r.Name == "backend");
        Assert.True(backend.IsDefault);
        var frontend = project.Repositories.First(r => r.Name == "frontend");
        Assert.False(frontend.IsDefault);
    }

    [Fact]
    public async Task SetDefaultRepository_OnCurrentDefault_IsIdempotent()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var grain = NewProjectGrain(id);
        await grain.CreateAsync(
            "idempotent-default",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        var before = await grain.GetAsync();
        await grain.SetDefaultRepositoryAsync("server");

        var activeAfter = await grain.GetAsync();
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = NewProjectGrain(id);
        await reactivated.SetDefaultRepositoryAsync("server");
        var after = await reactivated.GetAsync();

        Assert.NotNull(before);
        Assert.NotNull(activeAfter);
        Assert.NotNull(after);
        Assert.Equal(before!.UpdatedAt, activeAfter!.UpdatedAt);
        Assert.Equal(DateTimeOffset.Parse(before.UpdatedAt), DateTimeOffset.Parse(after!.UpdatedAt));
        Assert.Equal(
            before.Repositories.Select(repository => (repository.Name, repository.GitUrl, repository.BaseBranch, repository.IsDefault)),
            activeAfter.Repositories.Select(repository => (repository.Name, repository.GitUrl, repository.BaseBranch, repository.IsDefault)));
        Assert.Equal(
            before.Repositories.Select(repository => (repository.Name, repository.GitUrl, repository.BaseBranch, repository.IsDefault)),
            after.Repositories.Select(repository => (repository.Name, repository.GitUrl, repository.BaseBranch, repository.IsDefault)));
    }

    [Fact]
    public async Task SetDefaultRepository_OnUnknown_ReturnsNull()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "unknown-default",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        var result = await grain.SetDefaultRepositoryAsync("ghost");
        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveRepository_NonDefault_RemovesWithoutChangingDefault()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "remove-test",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.AddRepositoryAsync("web", "git@example.com:web.git", "main");

        var updated = await grain.RemoveRepositoryAsync("web");

        Assert.NotNull(updated);
        Assert.Single(updated!.Repositories);
        Assert.True(updated.Repositories[0].IsDefault);
    }

    [Fact]
    public async Task RemoveRepository_Default_RejectedAsConflict()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "remove-default",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.RemoveRepositoryAsync("server"));
        Assert.Contains("default", ex.Message, StringComparison.OrdinalIgnoreCase);

        var after = await grain.GetAsync();
        Assert.Single(after!.Repositories);
        Assert.True(after.Repositories[0].IsDefault);
    }

    [Fact]
    public async Task RemoveRepository_Unknown_ReturnsNull()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "remove-unknown",
            new RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        var result = await grain.RemoveRepositoryAsync("ghost");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateRepository_ChangesGitUrlAndBaseBranch()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "repo-update",
            new RepositoryInfo
            {
                Name = "backend",
                GitUrl = "git@example.com:backend.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        var updated = await grain.UpdateRepositoryAsync("backend", gitUrl: "git@example.com:backend-v2.git", baseBranch: "develop");

        Assert.NotNull(updated);
        var repo = updated!.Repositories.Single();
        Assert.Equal("git@example.com:backend-v2.git", repo.GitUrl);
        Assert.Equal("develop", repo.BaseBranch);
    }

    [Fact]
    public async Task UpdateRepository_EmptyPatch_ReturnsExistingWithoutMutation()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "repo-update-empty",
            new RepositoryInfo
            {
                Name = "backend",
                GitUrl = "git@example.com:backend.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.UpdateRepositoryAsync("backend", gitUrl: null, baseBranch: null));
    }

    [Fact]
    public async Task UpdateRepository_UnknownName_ReturnsNull()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "repo-update-unknown",
            new RepositoryInfo
            {
                Name = "backend",
                GitUrl = "git@example.com:backend.git",
                BaseBranch = "main",
                IsDefault = true,
            });

        var result = await grain.UpdateRepositoryAsync("ghost", gitUrl: "git@example.com:other.git");
        Assert.Null(result);
    }

    [Fact]
    public async Task DefaultRepository_ReturnsFlaggedRepositoryWithoutFallback()
    {
        var grain = NewProjectGrain();
        await grain.CreateAsync(
            "default-repository-lookup",
            new RepositoryInfo
            {
                Name = "first-listed",
                GitUrl = "git@example.com:first.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.AddRepositoryAsync("default-one", "git@example.com:d.git", "main", setDefault: true);

        var project = await grain.GetAsync();
        Assert.NotNull(project);
        Assert.NotNull(project!.DefaultRepository);
        Assert.Equal("default-one", project.DefaultRepository!.Name);
        Assert.True(project.DefaultRepository.IsDefault);
    }
}
