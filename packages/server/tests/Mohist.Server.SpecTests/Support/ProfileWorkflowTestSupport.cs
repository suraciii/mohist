using Mohist.Server.TestSupport;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public class FakePromptLoader : IPromptLoader
{
    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal)
    {
        ["proposal"] = "# Proposal Artifact\nCreate proposal.md",
        ["specs"] = "# Specs Artifact\nCreate specs",
        ["design"] = "# Design Artifact\nCreate design.md",
        ["tasks"] = "# Tasks Artifact\nCreate tasks.json",
        ["self-review"] = "# Self Review\nReview artifacts",
        ["review"] = "# Review\nReview implementation",
        ["build"] = "# Build\nImplement task",
    };

    public string Load(string name) => Prompts.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"Prompt '{name}' not found");
    public Dictionary<string, string> LoadAll() => new(Prompts, StringComparer.Ordinal);
}

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
