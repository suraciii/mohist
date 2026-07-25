using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class DatabaseInitializerRepositoryUpgradeSpecs
{
    [Fact]
    public async Task InitializeAsync_AfterMigration_UpgradesProjectRepositories()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(database.Keeper))
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
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

            var initializer = new MohistDatabaseInitializer();
            await initializer.InitializeAsync(services, CancellationToken.None);

            using var assertionScope = services.CreateScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var project = await assertionDb.Projects.SingleAsync();
            var repository = Assert.Single(JSON.Deserialize<List<RepositoryInfo>>(project.RepositoriesJson)!);
            Assert.True(repository.IsDefault);
            Assert.Equal("git@example.com:server.git", repository.GitUrl);
            Assert.Equal("release", repository.BaseBranch);
        }
    }

    [Fact]
    public async Task InitializeAsync_WorkflowProfileCollision_RollsBackLegacyConversion()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        const string projectId = "proj_workflow_collision";
        var originalTemplate = JsonProfile();
        var targetId = $"{WorkflowProfileDataMigrator.ReservedIdPrefix}bW9oaXN0L2xvY2Fs";
        await using (var db = new MohistDbContext(database.Options))
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = "mohist/local",
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = "mohist/local",
                Template = originalTemplate,
            });
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = targetId,
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(database.Keeper))
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .BuildServiceProvider();
        await using (services)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseInitializer.InitializeAsync(services));

            await using var assertionDb = new MohistDbContext(database.Options);
            var legacy = await assertionDb.ProjectWorkflowTemplates.SingleAsync();
            Assert.Equal(originalTemplate, legacy.Template);
            var profile = await assertionDb.ProjectWorkflowProfiles.SingleAsync();
            Assert.Equal("mohist/local", profile.DefaultWorkflowProfileId);
            Assert.Single(await assertionDb.WorkflowProfileRecords.ToListAsync());
        }
    }

    private static string JsonProfile() =>
        "{\"id\":\"legacy-custom\",\"name\":\"Legacy\",\"description\":\"\",\"definition\":{\"stages\":[{\"stage\":\"build\",\"tasks\":[],\"checks\":[]}]}}";
}
