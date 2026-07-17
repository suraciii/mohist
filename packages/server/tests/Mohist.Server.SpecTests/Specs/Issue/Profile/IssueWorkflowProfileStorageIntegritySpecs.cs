using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public class IssueWorkflowProfileStorageIntegritySpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly TestDbContextFactory _factory;

    public IssueWorkflowProfileStorageIntegritySpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Verify_EmptyDatasetIsHealthy()
    {
        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.Equal(0, report.Scanned);
        Assert.True(report.IsHealthy);
        Assert.Empty(report.UnreachableIssues);
    }

    [Fact]
    public async Task Verify_ReportsIssueScopedAgentPaths()
    {
        await SeedProfileAsync("project_1", 1, """{"vars":{"agent":{"model":"gpt-5"}}}""");
        await SeedProfileAsync("project_1", 2, """{"stages":{"build":{"vars":{"agent":{"model":"gpt-5"}}}}}""");

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.True(report.IsHealthy);
        Assert.Contains(report.Rows, row => row is { ProjectId: "project_1", IssueNumber: 1, AgentPath: "vars.agent" });
        Assert.Contains(report.Rows, row => row is { ProjectId: "project_1", IssueNumber: 2, AgentPath: "stages.build.vars.agent" });
    }

    [Fact]
    public void InspectVariables_RequiresScopedIdentity()
    {
        Assert.Throws<ArgumentException>(() => IssueWorkflowProfileStorageIntegrity.InspectVariables("", 1, "{}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => IssueWorkflowProfileStorageIntegrity.InspectVariables("project_1", 0, "{}"));
    }

    [Fact]
    public async Task DefensiveCopy_WritesAgentDataToTheScopedProfile()
    {
        await SeedProfileAsync("project_1", 7, "{}");

        await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "project_1",
            7,
            new Dictionary<string, object?> { ["model"] = "gpt-5" },
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["build"] = new() { ["model"] = "gpt-5-mini" },
            });

        await using var db = new MohistDbContext(_database.Options);
        var row = await db.IssueWorkflowProfiles.SingleAsync(profile =>
            profile.ProjectId == "project_1" && profile.IssueNumber == 7);
        using var document = JsonDocument.Parse(row.Variables);
        Assert.Equal("gpt-5", document.RootElement.GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("gpt-5-mini", document.RootElement.GetProperty("stages").GetProperty("build").GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task DefensiveCopy_RejectsMissingScopedProfile()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
                _factory,
                "project_1",
                1,
                new Dictionary<string, object?> { ["model"] = "gpt-5" },
                null));
    }

    private async Task SeedProfileAsync(string projectId, int issueNumber, string variables)
    {
        await using var db = new MohistDbContext(_database.Options);
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Variables = variables,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

}
