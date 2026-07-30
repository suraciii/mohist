using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Services;

public sealed class WorkflowPromptResolverSpecs : IAsyncLifetime
{
    private const string ProjectId = "proj-prompt-resolver";
    private const string RunId = "wr-prompt-resolver";
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly WorkflowPromptResolver _resolver;

    public WorkflowPromptResolverSpecs()
    {
        var factory = new TestDbContextFactory(_database.Options);
        var loader = new InMemoryPromptLoader(
        [
            new SystemTemplate("proposal", "Proposal", "", [], null, "system body"),
        ]);
        _resolver = new WorkflowPromptResolver(
            factory,
            new ProjectPromptStore(factory, loader, new PromptTemplateEngine()));
    }

    [Fact]
    public async Task LoadPrompt_PrefersProjectBodyAndFallsBackToSystem()
    {
        await SeedRunAsync();
        await SetPromptAsync("project body");

        var project = await _resolver.LoadPromptAsync(RunId, "proposal");
        var system = await _resolver.LoadPromptAsync(RunId, "proposal", "other-project");
        var unknown = await _resolver.LoadPromptAsync(RunId, "missing");

        Assert.Equal(("project", "project body"), (project!.Source, project.Body));
        Assert.Equal(("system", "system body"), (system!.Source, system.Body));
        Assert.Null(unknown);
    }

    [Fact]
    public async Task LoadPrompts_UsesRunProjectAndAppliesStageFilter()
    {
        await SeedRunAsync();
        await SetPromptAsync("project body");

        var prompts = await _resolver.LoadPromptsAsync(RunId);

        var prompt = Assert.Single(prompts);
        Assert.Equal("project body", prompt.Body);
        Assert.Equal("project", prompt.Source);
    }

    private async Task SeedRunAsync()
    {
        var definition = new WorkflowDefinition([new StageDefinition("build", [], [])]);
        var run = WorkflowRunExtensions.Create(
            RunId,
            definition,
            DateTimeOffset.UnixEpoch,
            new WorkflowRunMetadata(
                null,
                DateTimeOffset.UnixEpoch,
                ProjectId: ProjectId,
                IssueNumber: 1));

        await using var db = new MohistDbContext(_database.Options);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = RunId,
            State = JsonSerializer.Serialize(run),
        });
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile { ProjectId = ProjectId });
        await db.SaveChangesAsync();
    }

    private async Task SetPromptAsync(string body)
    {
        await using var db = new MohistDbContext(_database.Options);
        var profile = await db.ProjectWorkflowProfiles.SingleAsync(row => row.ProjectId == ProjectId);
        profile.Prompts = new Dictionary<string, string>(StringComparer.Ordinal) { ["proposal"] = body };
        await db.SaveChangesAsync();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }
}
