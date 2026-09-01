using Mohist.Server.TestSupport;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.L1Tests.Support;

namespace Mohist.Server.L1Tests.Specs.Issue.Profile;

public sealed class FakeDbContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly TestSqliteDatabase _database;

    public FakeDbContextFactory(Dictionary<string, string>? projectPrompts = null, string? projectId = null)
    {
        _database = TestSqliteDatabase.CreateMigrated();
        using var db = CreateDbContext();
        if (projectPrompts is { Count: > 0 } && projectId is not null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Prompts = projectPrompts,
            });
            db.SaveChanges();
        }
    }

    public MohistDbContext CreateDbContext() => _database.CreateContext();

    public void Dispose() => _database.Dispose();
}
