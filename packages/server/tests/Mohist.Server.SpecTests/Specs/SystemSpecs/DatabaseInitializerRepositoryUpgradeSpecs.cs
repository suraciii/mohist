using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Project.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class DatabaseInitializerRepositoryUpgradeSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InitializeAsync_AfterMigration_UpgradesProjectRepositories()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        MigratedSqliteTemplate.CopyTo(connection);
        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(connection))
            .BuildServiceProvider();
        await using (services)
        {
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
                db.Projects.Add(new ProjectRow
                {
                    Id = "proj_legacy",
                    Name = "legacy",
                    RepositoriesJson = JSON.Serialize(new[]
                    {
                        new RepositoryInfo
                        {
                            Name = "server",
                            GitUrl = "git@example.com:server.git",
                            BaseBranch = "release",
                            IsDefault = false,
                        },
                    }),
                });
                await db.SaveChangesAsync();
            }

            await DatabaseInitializer.InitializeAsync(services);

            using var assertionScope = services.CreateScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var project = await assertionDb.Projects.SingleAsync();
            var repository = Assert.Single(JSON.Deserialize<List<RepositoryInfo>>(project.RepositoriesJson)!);
            Assert.True(repository.IsDefault);
            Assert.Equal("git@example.com:server.git", repository.GitUrl);
            Assert.Equal("release", repository.BaseBranch);
        }
    }
}
