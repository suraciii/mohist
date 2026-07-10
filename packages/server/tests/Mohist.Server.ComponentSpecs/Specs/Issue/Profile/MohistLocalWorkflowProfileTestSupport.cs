using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.ComponentSpecs.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Specs.Issue.Profile;

internal sealed class FakeDbContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly SqliteConnection _connection;

    public FakeDbContextFactory(Dictionary<string, string>? projectPrompts = null, string? projectId = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
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

    public MohistDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(_connection).Options;
        return new MohistDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}
