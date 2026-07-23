using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

/// <summary>
/// End-to-end spec coverage for issue-131 T-001: server-owned virtual
/// <c>mohist/agent</c> Action manifest. Profile save must accept valid
/// task references without querying the current Agent state, reject
/// template expressions on <c>name</c>, reject any usage on checks, and
/// keep inline <c>mohist/opencode</c> and <c>mohist/pi</c> behavior
/// unchanged. Checks are unsupported, not silently ignored.
/// </summary>
public class VirtualAgentActionValidationSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;

    public VirtualAgentActionValidationSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    // =======================================================================
    // Empty catalog: virtual entry is server-augmented, so save still succeeds
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_AgentTask_WithEmptyCatalog_SucceedsWithoutAgentState()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var result = await manager.CreateTemplateAsync("proj-agent-precedes", """
            id: agent-task-profile
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

        Assert.Equal("proj-agent-precedes", result.Template.ProjectId);
        Assert.Equal("agent-task-profile", result.Template.TemplateId);
        Assert.Equal(ActionValidationStatus.Performed, result.ActionValidation);
    }

    // =======================================================================
    // Runner-supplied catalog: server-owned manifest always wins
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_AgentTask_WithInlineCatalog_StillSucceeds()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var result = await manager.CreateTemplateAsync("proj-agent-with-runner", """
            id: agent-and-runner-profile
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

        Assert.Equal("agent-and-runner-profile", result.Template.TemplateId);
    }

    // =======================================================================
    // Missing name / prompt
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_AgentTask_MissingName_Rejected()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-agent-missing-name", """
                stages:
                  - stage: build
                    tasks:
                      - id: review
                        uses: mohist/agent
                        with:
                          prompt: Audit this change.
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.name", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("missing required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTemplate_AgentTask_MissingPrompt_Rejected()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-agent-missing-prompt", """
                stages:
                  - stage: build
                    tasks:
                      - id: review
                        uses: mohist/agent
                        with:
                          name: reviewer
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.prompt", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("missing required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTemplate_AgentTask_UnknownInput_Rejected()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-agent-unknown-input", """
                stages:
                  - stage: build
                    tasks:
                      - id: review
                        uses: mohist/agent
                        with:
                          name: reviewer
                          prompt: Audit this change.
                          runtime: pi
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.runtime", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("unknown input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =======================================================================
    // Template-on-name rejection
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_AgentTask_TemplateName_Rejected()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-agent-template-name", """
                stages:
                  - stage: build
                    tasks:
                      - id: review
                        uses: mohist/agent
                        with:
                          name: ${{ vars.reviewerName }}
                          prompt: Audit this change.
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.name", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("template", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("literal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTemplate_AgentTask_TemplatePrompt_Accepted()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var result = await manager.CreateTemplateAsync("proj-agent-template-prompt", """
            stages:
              - stage: build
                tasks:
                  - id: review
                    uses: mohist/agent
                    with:
                      name: reviewer
                      prompt: ${{ prompts.review }}
                checks: []
            """);

        Assert.Equal("proj-agent-template-prompt", result.Template.ProjectId);
    }

    [Fact]
    public async Task CreateTemplate_AgentTask_TemplateSessionAndTimeout_Accepted()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var result = await manager.CreateTemplateAsync("proj-agent-template-extras", """
            stages:
              - stage: build
                tasks:
                  - id: review
                    uses: mohist/agent
                    with:
                      name: reviewer
                      prompt: Audit this change.
                      session: ${{ vars.reviewSession }}
                      timeout: ${{ vars.reviewTimeoutMs }}
                checks: []
            """);

        Assert.Equal("proj-agent-template-extras", result.Template.ProjectId);
    }

    // =======================================================================
    // Check rejection
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_AgentCheck_Rejected()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-agent-check", """
                stages:
                  - stage: build
                    tasks:
                      - id: review
                        uses: mohist/agent
                        with:
                          name: reviewer
                          prompt: Audit this change.
                    checks:
                      - id: audit
                        uses: mohist/agent
                        with:
                          name: reviewer
                          prompt: Audit this change.
                """));

        var checkError = Assert.Single(exception.Errors, e => e.Path == "stages[0].checks[0]");
        Assert.Equal(ValidationSource.Action, checkError.Source);
        Assert.Contains("check", checkError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mohist/agent", checkError.Message);
    }

    [Fact]
    public async Task CreateTemplate_AgentCheck_Rejected_EvenWithMalformedInputs()
    {
        var manager = BuildManagerWithCatalog(new ActionCatalog([], []));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-agent-check-malformed", """
                stages:
                  - stage: build
                    tasks: []
                    checks:
                      - id: audit
                        uses: mohist/agent
                        with:
                          name: ${{ vars.x }}
                """));

        var checkError = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].checks[0]", checkError.Path);
        Assert.Equal(ValidationSource.Action, checkError.Source);
        Assert.Contains("check", checkError.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =======================================================================
    // Inline Action behavior is unchanged
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_InlineActions_StillValidatedByCatalog()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-inline-still-validated", """
                stages:
                  - stage: build
                    tasks:
                      - id: build
                        uses: mohist/opencode
                        with:
                          prompt: Compile the project.
                          agent: legacy
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.agent", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("unknown input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTemplate_AgentAndInline_Together_Succeed()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var result = await manager.CreateTemplateAsync("proj-agent-and-inline", """
            stages:
              - stage: build
                tasks:
                  - id: compile
                    uses: mohist/opencode
                    with:
                      prompt: Compile the project.
                  - id: review
                    uses: mohist/agent
                    with:
                      name: reviewer
                      prompt: Audit this change.
                checks: []
            """);

        Assert.Equal("proj-agent-and-inline", result.Template.ProjectId);
    }

    // =======================================================================
    // AgentNotFound error code is documented in the manifest errors
    // =======================================================================

    [Fact]
    public void MohistAgentManifest_DeclaresAgentNotFoundError()
    {
        var entry = VirtualActionManifests.MohistAgent;
        Assert.Equal("mohist/agent", entry.Name);
        Assert.Contains(entry.Errors, e => string.Equals(e.Code, "agent_not_found", StringComparison.Ordinal));
        Assert.Contains(entry.Inputs, i => i.Name == "name" && i.Required);
        Assert.Contains(entry.Inputs, i => i.Name == "prompt" && i.Required);
        Assert.Contains(entry.Inputs, i => i.Name == "session" && !i.Required);
        Assert.Contains(entry.Inputs, i => i.Name == "timeout" && !i.Required);
    }

    // =======================================================================
    // Issue-level path: identical behavior
    // =======================================================================

    [Fact]
    public async Task IssueTemplateUpdate_AgentTask_Accepted()
    {
        var dbFactory = new TestDbContextFactory(_database.Options);
        var manager = new IssueWorkflowProfileManager(
            dbFactory, new StubActionCatalogSource(new ActionCatalog([], [])));

        var result = await manager.UpdateTemplateAsync("proj-issue-agent", 11, new IssueTemplateUpdateRequest(
            Template: """
                stages:
                  - stage: build
                    tasks:
                      - id: review
                        uses: mohist/agent
                        with:
                          name: reviewer
                          prompt: Audit this change.
                    checks: []
                """));

        Assert.Equal(ActionValidationStatus.Performed, result.ActionValidation);
        Assert.NotNull(result.State.Template);
        Assert.Equal("mohist/agent", result.State.Template!.Definition.Stages[0].Tasks[0].Uses);
    }

    [Fact]
    public async Task IssueTemplateUpdate_AgentCheck_Rejected()
    {
        var dbFactory = new TestDbContextFactory(_database.Options);
        var manager = new IssueWorkflowProfileManager(
            dbFactory, new StubActionCatalogSource(new ActionCatalog([], [])));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.UpdateTemplateAsync("proj-issue-agent-check", 12, new IssueTemplateUpdateRequest(
                Template: """
                    stages:
                      - stage: build
                        tasks: []
                        checks:
                          - id: audit
                            uses: mohist/agent
                            with:
                              name: reviewer
                              prompt: Audit this change.
                    """)));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].checks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private ProjectWorkflowProfileManager BuildManagerWithCatalog(ActionCatalog catalog) =>
        new(new TestDbContextFactory(_database.Options),
            new StubPromptLoader(),
            new PromptTemplateEngine(),
            new StubActionCatalogSource(catalog));

    private sealed class StubPromptLoader : IPromptLoader
    {
        public Dictionary<string, string> LoadAll() => new(StringComparer.Ordinal);
    }
}