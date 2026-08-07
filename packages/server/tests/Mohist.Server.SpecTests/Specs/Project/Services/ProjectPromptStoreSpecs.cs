using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Services;

public sealed class ProjectPromptStoreSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateModelSchema();
    private readonly ProjectPromptStore _store;
    private readonly InMemoryPromptLoader _loader;

    public ProjectPromptStoreSpecs()
    {
        _loader = new InMemoryPromptLoader(
        [
            new SystemTemplate("proposal", "Proposal", "system proposal", ["plan"], "plan", "system body"),
            new SystemTemplate("build", "Build", "system build", ["build"], "build", "build body"),
            new SystemTemplate("plain", "Plain", "system plain", [], null, "plain body"),
        ]);
        var factory = new TestDbContextFactory(_database.Options);
        _store = new ProjectPromptStore(factory, _loader, new PromptTemplateEngine());
    }

    [Fact]
    public async Task ListPrompts_MergesSystemOverrideAndNewKeyWithSources()
    {
        await _store.SetPromptAsync("proj-prompts", "proposal", "project body");
        await _store.SetPromptAsync("proj-prompts", "custom", "custom body");

        var prompts = await _store.ListPromptsAsync("proj-prompts");

        Assert.Equal(["build", "custom", "plain", "proposal"], prompts.Select(prompt => prompt.Key));
        Assert.Equal("project", prompts.Single(prompt => prompt.Key == "proposal").Source);
        Assert.Equal("project body", prompts.Single(prompt => prompt.Key == "proposal").Body);
        Assert.Equal("project-new", prompts.Single(prompt => prompt.Key == "custom").Source);
        Assert.Equal("system", prompts.Single(prompt => prompt.Key == "build").Source);
    }

    [Fact]
    public async Task ListPrompts_StageFilterIncludesUnstagedAndMatchingPrompts()
    {
        var prompts = await _store.ListPromptsAsync("proj-stages", "plan");

        Assert.Equal(["plain", "proposal"], prompts.Select(prompt => prompt.Key));
    }

    [Fact]
    public async Task DeletePrompt_RestoresSystemAndRemovesUnknownPrompt()
    {
        await _store.SetPromptAsync("proj-delete", "proposal", "project body");
        await _store.SetPromptAsync("proj-delete", "custom", "custom body");

        await _store.DeletePromptAsync("proj-delete", "proposal");
        await _store.DeletePromptAsync("proj-delete", "custom");

        var proposal = await _store.GetPromptAsync("proj-delete", "proposal");
        var custom = await _store.GetPromptAsync("proj-delete", "custom");
        Assert.Equal(("system", "system body"), (proposal!.Source, proposal.Body));
        Assert.Null(custom);
    }

    [Fact]
    public async Task PreviewPrompt_ReturnsEngineRenderDetails()
    {
        await _store.SetPromptAsync("proj-preview", "proposal", "Hello ${{ vars.name }} / ${{ vars.missing }}");
        using var document = JsonDocument.Parse("{\"vars\":{\"name\":\"Ada\"}}");

        var actual = await _store.PreviewPromptAsync("proj-preview", "proposal", document.RootElement);
        var expected = new PromptTemplateEngine().Render(
            "Hello ${{ vars.name }} / ${{ vars.missing }}",
            document.RootElement);

        Assert.Equal(expected.Rendered, actual.Rendered);
        Assert.Equal(expected.MissingVariables, actual.MissingVariables);
        Assert.Equal(expected.Depth, actual.Depth);
        Assert.Equal(expected.Errors, actual.Errors);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }
}
