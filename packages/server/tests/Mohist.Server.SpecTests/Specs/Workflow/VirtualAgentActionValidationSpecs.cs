using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

public class VirtualAgentActionValidationSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateModelSchema();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreateProfile_AgentTask_WithEmptyCatalog_SucceedsWithoutAgentState()
    {
        var result = await SaveAsync("proj-agent", new ActionCatalog([], []), """
            stages:
              - stage: build
                tasks:
                  - id: review
                    uses: mohist/agent
                    with:
                      name: reviewer
                      prompt: Audit this change.
                checks: []
            """);

        Assert.True(result.ValidationResult.IsValid);
        Assert.Equal(ActionValidationStatus.Performed, result.ValidationResult.ActionValidationStatus);
    }

    [Fact]
    public async Task CreateProfile_AgentTask_MissingName_ReturnsActionError()
    {
        var result = await SaveAsync("proj-agent-missing-name", new ActionCatalog([], []), """
            stages:
              - stage: build
                tasks:
                  - id: review
                    uses: mohist/agent
                    with:
                      prompt: Audit this change.
                checks: []
            """);

        var error = Assert.Single(result.ValidationResult.ActionErrors);
        Assert.Equal("stages[0].tasks[0].with.name", error.Path);
    }

    [Fact]
    public async Task CreateProfile_AgentTask_RejectsTemplateName()
    {
        var result = await SaveAsync("proj-agent-template-name", new ActionCatalog([], []), """
            stages:
              - stage: build
                tasks:
                  - id: review
                    uses: mohist/agent
                    with:
                      name: ${{ vars.reviewerName }}
                      prompt: Audit this change.
                checks: []
            """);

        var error = Assert.Single(result.ValidationResult.ActionErrors);
        Assert.Equal("stages[0].tasks[0].with.name", error.Path);
        Assert.Contains("literal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateProfile_AgentCheck_ReturnsActionError()
    {
        var result = await SaveAsync("proj-agent-check", new ActionCatalog([], []), """
            stages:
              - stage: build
                tasks: []
                checks:
                  - id: audit
                    uses: mohist/agent
                    with:
                      name: reviewer
                      prompt: Audit this change.
            """);

        var error = Assert.Single(result.ValidationResult.ActionErrors);
        Assert.Equal("stages[0].checks[0]", error.Path);
    }

    [Fact]
    public async Task CreateProfile_InlineAction_StillValidatesCatalogInputs()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode", [new ActionCatalogInput("prompt", ["string"], true)], [], [])],
            []);
        var result = await SaveAsync("proj-inline", catalog, """
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                    with:
                      prompt: Compile the project.
                      agent: legacy
                checks: []
            """);

        var error = Assert.Single(result.ValidationResult.ActionErrors);
        Assert.Equal("stages[0].tasks[0].with.agent", error.Path);
    }

    private async Task<WorkflowProfileSaveResult> SaveAsync(string projectId, ActionCatalog catalog, string yaml)
    {
        var provider = new WorkflowProfileProvider(
            new TestDbContextFactory(_database.Options),
            new StubActionCatalogSource(catalog));
        return await provider.CreateAsync(projectId, new WorkflowProfileCollectionEntry(
            projectId,
            projectId + "/profile",
            "Profile",
            string.Empty,
            WorkflowProfileSourceProvenance.Verbatim,
            false,
            yaml));
    }
}
