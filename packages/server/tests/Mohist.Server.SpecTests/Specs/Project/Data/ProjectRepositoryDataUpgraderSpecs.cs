using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Project.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Data;

public class ProjectRepositoryDataUpgraderSpecs
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly DateTimeOffset UpdatedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z");

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task UpgradeAsync_NormalizesDefaultsAndPreservesRepositoryMetadataAndProjectIdentity()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        await SeedProjectAsync(db, "proj_single", "single", [
            Repository("server", "git@example.com:server.git", "release", false),
        ]);
        await SeedProjectAsync(db, "proj_missing", "missing", [
            Repository("web", "git@example.com:web.git", "develop", false),
            Repository("server", "git@example.com:server.git", "main", false),
        ]);
        await SeedProjectAsync(db, "proj_multiple", "multiple", [
            Repository("first", "git@example.com:first.git", "one", false),
            Repository("second", "git@example.com:second.git", "two", true),
            Repository("third", "git@example.com:third.git", "three", true),
        ]);
        var validJson = "[ { \"name\": \"api\", \"gitUrl\": \"git@example.com:api.git\", \"baseBranch\": \"stable\", \"isDefault\": false }, { \"name\": \"ui\", \"gitUrl\": \"git@example.com:ui.git\", \"baseBranch\": \"main\", \"isDefault\": true } ]";
        await SeedProjectJsonAsync(db, "proj_valid", "valid", validJson);

        await ProjectRepositoryDataUpgrader.UpgradeAsync(db);

        var single = await LoadProjectAsync(db, "proj_single");
        AssertRepositories(single, ("server", "git@example.com:server.git", "release", true));
        var missing = await LoadProjectAsync(db, "proj_missing");
        AssertRepositories(
            missing,
            ("web", "git@example.com:web.git", "develop", true),
            ("server", "git@example.com:server.git", "main", false));
        var multiple = await LoadProjectAsync(db, "proj_multiple");
        AssertRepositories(
            multiple,
            ("first", "git@example.com:first.git", "one", false),
            ("second", "git@example.com:second.git", "two", true),
            ("third", "git@example.com:third.git", "three", false));
        var valid = await LoadProjectAsync(db, "proj_valid");
        AssertRepositories(
            valid,
            ("api", "git@example.com:api.git", "stable", false),
            ("ui", "git@example.com:ui.git", "main", true));
        Assert.Equal(validJson, valid.RepositoriesJson);
        Assert.Equal("proj_single", single.Id);
        Assert.Equal(CreatedAt, single.CreatedAt);
        Assert.Equal(UpdatedAt, single.UpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task UpgradeAsync_WhenCalledTwice_IsIdempotent()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        await SeedProjectAsync(db, "proj_legacy", "legacy", [
            Repository("server", "git@example.com:server.git", "main", false),
        ]);

        await ProjectRepositoryDataUpgrader.UpgradeAsync(db);
        var firstJson = (await LoadProjectAsync(db, "proj_legacy")).RepositoriesJson;
        await ProjectRepositoryDataUpgrader.UpgradeAsync(db);

        Assert.Equal(firstJson, (await LoadProjectAsync(db, "proj_legacy")).RepositoriesJson);
    }

    public static TheoryData<string, string, string> InvalidProjects => new()
    {
        { "[]", "Project 'broken' (proj_broken)", "at least one repository" },
        { "not-json", "Project 'broken' (proj_broken)", "malformed" },
        { "[null]", "Project 'broken' (proj_broken)", "null repository declaration" },
        { JSON.Serialize(new[] { Repository("", "git@example.com:server.git", "main", false) }), "Project 'broken' (proj_broken)", "repositories[0].name" },
        { JSON.Serialize(new[] { Repository("server", "", "main", false) }), "Project 'broken' (proj_broken)", "repositories[0].gitUrl" },
        { JSON.Serialize(new[] { Repository("server", "git@example.com:server.git", "", false) }), "Project 'broken' (proj_broken)", "repositories[0].baseBranch" },
        { "[{\"name\":null,\"gitUrl\":\"git@example.com:server.git\",\"baseBranch\":\"main\",\"isDefault\":false}]", "Project 'broken' (proj_broken)", "repositories[0].name" },
        { "[{\"name\":\"server\",\"gitUrl\":null,\"baseBranch\":\"main\",\"isDefault\":false}]", "Project 'broken' (proj_broken)", "repositories[0].gitUrl" },
        { "[{\"name\":\"server\",\"gitUrl\":\"git@example.com:server.git\",\"baseBranch\":null,\"isDefault\":false}]", "Project 'broken' (proj_broken)", "repositories[0].baseBranch" },
        { JSON.Serialize(new[] { Repository("server", "git@example.com:one.git", "main", false), Repository("SERVER", "git@example.com:two.git", "main", false) }), "Project 'broken' (proj_broken)", "Duplicate repository name 'SERVER'" },
    };

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Theory]
    [MemberData(nameof(InvalidProjects))]
    public async Task UpgradeAsync_UnrecoverableProject_FailsWithDiagnosticAndLeavesAllRowsUnchanged(
        string invalidJson,
        string projectDiagnostic,
        string declarationDiagnostic)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var validJson = JSON.Serialize(new[] {
            Repository("server", "git@example.com:server.git", "release", false),
        });
        await SeedProjectJsonAsync(db, "proj_valid", "valid", validJson);
        await SeedProjectJsonAsync(db, "proj_broken", "broken", invalidJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectRepositoryDataUpgrader.UpgradeAsync(db));

        Assert.Contains(projectDiagnostic, exception.Message);
        Assert.Contains(declarationDiagnostic, exception.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(validJson, (await LoadProjectAsync(db, "proj_valid")).RepositoriesJson);
        Assert.Equal(invalidJson, (await LoadProjectAsync(db, "proj_broken")).RepositoriesJson);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task UpgradeAsync_WhenPersistenceFails_RollsBackEveryPreparedProject()
    {
        await using var connection = await OpenDatabaseAsync();
        var firstJson = JSON.Serialize(new[] { Repository("server", "git@example.com:server.git", "main", false) });
        var secondJson = JSON.Serialize(new[] { Repository("web", "git@example.com:web.git", "develop", false) });
        await using (var seed = CreateContext(connection))
        {
            await SeedProjectJsonAsync(seed, "proj_first", "first", firstJson);
            await SeedProjectJsonAsync(seed, "proj_second", "second", secondJson);
        }

        await using (var failing = CreateContext(connection, new ThrowOnSaveChangesInterceptor()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectRepositoryDataUpgrader.UpgradeAsync(failing));
        }

        await using var verify = CreateContext(connection);
        Assert.Equal(firstJson, (await LoadProjectAsync(verify, "proj_first")).RepositoriesJson);
        Assert.Equal(secondJson, (await LoadProjectAsync(verify, "proj_second")).RepositoriesJson);
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        MigratedSqliteTemplate.CopyTo(connection);
        return connection;
    }

    private static MohistDbContext CreateContext(
        SqliteConnection connection,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection);
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new MohistDbContext(options.Options);
    }

    private static RepositoryInfo Repository(
        string name,
        string gitUrl,
        string baseBranch,
        bool isDefault) => new()
        {
            Name = name,
            GitUrl = gitUrl,
            BaseBranch = baseBranch,
            IsDefault = isDefault,
        };

    private static Task SeedProjectAsync(
        MohistDbContext db,
        string id,
        string name,
        IReadOnlyList<RepositoryInfo> repositories) =>
        SeedProjectJsonAsync(db, id, name, JSON.Serialize(repositories));

    private static async Task SeedProjectJsonAsync(
        MohistDbContext db,
        string id,
        string name,
        string repositoriesJson)
    {
        db.Projects.Add(new ProjectRow
        {
            Id = id,
            Name = name,
            RepositoriesJson = repositoriesJson,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ProjectRow> LoadProjectAsync(MohistDbContext db, string id)
    {
        db.ChangeTracker.Clear();
        return await db.Projects.SingleAsync(project => project.Id == id);
    }

    private static void AssertRepositories(
        ProjectRow project,
        params (string Name, string GitUrl, string BaseBranch, bool IsDefault)[] expected)
    {
        var actual = JSON.Deserialize<List<RepositoryInfo>>(project.RepositoriesJson)!;
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].GitUrl, actual[index].GitUrl);
            Assert.Equal(expected[index].BaseBranch, actual[index].BaseBranch);
            Assert.Equal(expected[index].IsDefault, actual[index].IsDefault);
        }
    }

    private sealed class ThrowOnSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected persistence failure");
    }
}
