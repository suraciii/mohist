using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

/// <summary>
/// End-to-end spec coverage for issue-446 T-003: Profile save judges
/// every task and check position against the Runner-reported Action
/// catalog, merges Action errors with Definition errors under one
/// YAML-path rule, and reports the Action-validation status on the
/// success response. Covers the catalog-backed path, the no-catalog
/// skip path with notice, and the regression guarantee that dispatch-time
/// validation remains authoritative.
/// </summary>
public class ProfileActionValidationSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly ProjectWorkflowProfileManager _projectManager;
    private readonly IssueWorkflowProfileManager _issueManager;

    public ProfileActionValidationSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        var dbFactory = new TestDbContextFactory(_database.Options);
        _projectManager = new ProjectWorkflowProfileManager(
            dbFactory, NullActionCatalogSource.Instance);
        _issueManager = new IssueWorkflowProfileManager(dbFactory, NullActionCatalogSource.Instance);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    // =======================================================================
    // No-catalog skip path
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_NoCatalog_SucceedsAndReportsSkipped()
    {
        var result = await _projectManager.CreateTemplateAsync("proj-no-catalog", TemplateYaml("t1"));

        Assert.Equal("t1", result.Template.TemplateId);
        Assert.Equal(ActionValidationStatus.Skipped, result.ActionValidation);
    }

    [Fact]
    public async Task UpdateTemplate_NoCatalog_SucceedsAndReportsSkipped()
    {
        await _projectManager.CreateTemplateAsync("proj-no-catalog-upd", TemplateYaml("t1"));

        var result = await _projectManager.UpdateTemplateAsync("proj-no-catalog-upd", "t1",
            TemplateYaml("t1", stageName: "stage-2"));

        Assert.NotNull(result);
        Assert.Equal(ActionValidationStatus.Skipped, result!.ActionValidation);
    }

    [Fact]
    public async Task IssueTemplateUpdate_NoCatalog_SucceedsAndReportsSkipped()
    {
        var yaml = TemplateYaml("custom-yaml");
        var result = await _issueManager.UpdateTemplateAsync("proj-issue", 42,
            new IssueTemplateUpdateRequest(Template: yaml));

        Assert.Equal(ActionValidationStatus.Skipped, result.ActionValidation);
        Assert.NotNull(result.State.Template);
        Assert.Equal("custom-yaml", result.State.Template!.Id);
    }

    // =======================================================================
    // Catalog-backed path: unknown uses
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_UnknownUses_RejectedWithActionError()
    {
        var manager = BuildManagerWithCatalog(SimpleCatalog());

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-unknown-uses", """
                stages:
                  - stage: build
                    tasks:
                      - id: run
                        uses: mohist/unknown
                        with: {}
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("mohist/unknown", error.Message);
    }

    // =======================================================================
    // Catalog-backed path: tombstoned uses
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_TombstonedUses_RejectedWithRemovedGuidance()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode", [], [], [])],
            [new ActionCatalogTombstone("mohist/acp-agent", "Use mohist/opencode instead.")]);
        var manager = BuildManagerWithCatalog(catalog);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-tombstone", """
                stages:
                  - stage: build
                    tasks:
                      - id: run
                        uses: mohist/acp-agent
                        with: {}
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("removed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mohist/opencode instead", error.Message);
    }

    [Fact]
    public async Task CreateTemplate_TombstoneAndUnknownProduceDistinctMessages()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode", [], [], [])],
            [new ActionCatalogTombstone("mohist/legacy-agent", "Migrate to mohist/opencode.")]);
        var manager = BuildManagerWithCatalog(catalog);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-mix", """
                stages:
                  - stage: build
                    tasks:
                      - id: tombstone-task
                        uses: mohist/legacy-agent
                        with: {}
                      - id: unknown-task
                        uses: mohist/never-existed
                        with: {}
                    checks: []
                """));

        Assert.Equal(2, exception.Errors.Count);
        var tombstoneError = Assert.Single(exception.Errors, e =>
            e.Path == "stages[0].tasks[0]" && e.Message.Contains("removed", StringComparison.OrdinalIgnoreCase));
        var unknownError = Assert.Single(exception.Errors, e =>
            e.Path == "stages[0].tasks[1]" && e.Message.Contains("unknown"));
        Assert.Equal(ValidationSource.Action, tombstoneError.Source);
        Assert.Equal(ValidationSource.Action, unknownError.Source);
    }

    // =======================================================================
    // Catalog-backed path: unknown with field (legacy agent/kind/type)
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_UnknownWithField_RejectedOnAction()
    {
        var manager = BuildManagerWithCatalog(SimpleCatalog());

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-unknown-with", """
                stages:
                  - stage: build
                    tasks:
                      - id: run
                        uses: mohist/opencode
                        with:
                          agent: legacy
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.agent", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("unknown input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateTemplate_LegacyKindAndType_RejectedOnAction()
    {
        var manager = BuildManagerWithCatalog(SimpleCatalog());

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-legacy-disc", """
                stages:
                  - stage: build
                    tasks:
                      - id: t1
                        uses: mohist/opencode
                        with:
                          kind: something
                      - id: t2
                        uses: mohist/opencode
                        with:
                          type: something
                    checks: []
                """));

        Assert.Equal(2, exception.Errors.Count);
        Assert.Contains(exception.Errors, e => e.Path == "stages[0].tasks[0].with.kind");
        Assert.Contains(exception.Errors, e => e.Path == "stages[0].tasks[1].with.type");
        Assert.All(exception.Errors, e => Assert.Equal(ValidationSource.Action, e.Source));
    }

    // =======================================================================
    // Catalog-backed path: missing required
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_MissingRequiredInput_RejectedOnAction()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-missing-required", """
                stages:
                  - stage: build
                    tasks:
                      - id: run
                        uses: mohist/opencode
                        with: {}
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.prompt", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("missing required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =======================================================================
    // Catalog-backed path: type mismatch
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_NumericStringForNumberInput_Rejected()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("timeout", ["number"], false)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-numeric-string", """
                stages:
                  - stage: build
                    tasks:
                      - id: run
                        uses: mohist/opencode
                        with:
                          timeout: "30"
                    checks: []
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0].with.timeout", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("number", error.Message);
        Assert.Contains("string", error.Message);
    }

    // =======================================================================
    // Catalog-backed path: template-valued declared input skips type check
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_TemplateValuedDeclaredInput_Accepted()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var result = await manager.CreateTemplateAsync("proj-template-input", """
            stages:
              - stage: build
                tasks:
                  - id: run
                    uses: mohist/opencode
                    with:
                      prompt: ${{ vars.buildPrompt }}
                checks: []
            """);

        Assert.Equal("proj-template-input", result.Template.ProjectId);
    }

    // =======================================================================
    // Catalog-backed path: engine-reserved working-directory is exempt
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_EngineReservedWorkingDirectory_NotRejected()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var result = await manager.CreateTemplateAsync("proj-working-dir", """
            stages:
              - stage: build
                tasks:
                  - id: run
                    uses: mohist/opencode
                    with:
                      working-directory: sub/dir
                      prompt: hi
                checks: []
            """);

        Assert.Equal("proj-working-dir", result.Template.ProjectId);
    }

    // =======================================================================
    // Catalog-backed path: every position is judged
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_AllPositionsAreJudgedAtSave()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-all-positions", """
                stages:
                  - stage: build
                    tasks:
                      - id: stage-task
                        uses: mohist/missing
                        with: {}
                      - id: with-recovery
                        uses: mohist/opencode
                        with:
                          prompt: ok
                        recovery:
                          budget: 1
                          handlers:
                            - tasks:
                                - id: recovery-task
                                  uses: mohist/missing
                                  with: {}
                              retrySelf: false
                    checks:
                      - id: stage-check
                        uses: mohist/missing
                        with: {}
                approval:
                  feedback:
                    tasks:
                      - id: approval-task
                        uses: mohist/missing
                        with: {}
                """));

        var paths = exception.Errors.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("stages[0].tasks[0]", paths);
        Assert.Contains("stages[0].checks[0]", paths);
        Assert.Contains("approval.feedback.tasks[0]", paths);
        Assert.Contains("stages[0].tasks[1].recovery.handlers[0].tasks[0]", paths);
        Assert.All(exception.Errors, e => Assert.Equal(ValidationSource.Action, e.Source));
    }

    // =======================================================================
    // Catalog-backed path: merged Definition + Action errors
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_DefinitionAndActionErrors_AreMergedAndDistinguishableBySource()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode",
                [new ActionCatalogInput("prompt", ["string"], true)],
                [],
                [])],
            []);
        var manager = BuildManagerWithCatalog(catalog);

        // Stage with no `stage` identifier produces a Definition error on
        // `stages[0].stage`; the unknown `uses` produces an Action error
        // on `stages[0].tasks[0]`. The two errors share the same parent
        // stage but are distinguishable by source.
        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-mixed-errors", """
                stages:
                  - tasks:
                      - id: bad-shape
                        uses: mohist/missing
                        with: {}
                    checks: []
                """));

        Assert.True(exception.Errors.Count >= 2,
            $"Expected at least 2 errors, got {exception.Errors.Count}");
        Assert.Contains(exception.Errors, e => e.Source == ValidationSource.Action && e.Path == "stages[0].tasks[0]");
        Assert.Contains(exception.Errors, e => e.Source == ValidationSource.Definition && e.Path == "stages[0].stage");
    }

    // =======================================================================
    // No second Definition rule owned by catalog check
    // =======================================================================

    [Fact]
    public async Task CreateTemplate_DefinitionOnlyError_LeavesCatalogCheckSilent()
    {
        var manager = BuildManagerWithCatalog(SimpleCatalog());

        // A stage with no `stage` identifier is a Definition-only error.
        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.CreateTemplateAsync("proj-definition-only", """
                stages:
                  - tasks:
                      - id: t1
                        uses: mohist/opencode
                        with: {}
                    checks: []
                """));

        Assert.NotEmpty(exception.Errors);
        Assert.All(exception.Errors, e => Assert.Equal(ValidationSource.Definition, e.Source));
    }

    // =======================================================================
    // Issue template path: identical behavior
    // =======================================================================

    [Fact]
    public async Task IssueTemplateUpdate_UnknownUses_RejectedWithActionError()
    {
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.Issues.Add(new IssueRow
            {
                State = JSON.Serialize(new { projectId = "proj-issue-action", number = 1 }),
            });
            await db.SaveChangesAsync();
        }
        var dbFactory = new TestDbContextFactory(_database.Options);
        var manager = new IssueWorkflowProfileManager(
            dbFactory, new StubActionCatalogSource(SimpleCatalog()));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            manager.UpdateTemplateAsync("proj-issue-action", 1, new IssueTemplateUpdateRequest(
                Template: """
                    stages:
                      - stage: build
                        tasks:
                          - id: t
                            uses: mohist/ghost
                            with: {}
                        checks: []
                    """)));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages[0].tasks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
    }

    [Fact]
    public async Task IssueTemplateUpdate_ProjectReference_DoesNotInvokeCatalogCheck()
    {
        var manager = new IssueWorkflowProfileManager(
            new TestDbContextFactory(_database.Options), new StubActionCatalogSource(SimpleCatalog()));

        var result = await manager.UpdateTemplateAsync("proj-issue-ref", 99,
            new IssueTemplateUpdateRequest(ProjectTemplateId: "t1"));

        Assert.Null(result.State.Template);
        Assert.Equal("t1", result.State.SourceTemplateId);
        Assert.Equal(ActionValidationStatus.Skipped, result.ActionValidation);
    }

    // =======================================================================
    // Built-in loading and runtime load remain Definition-only
    // =======================================================================

    [Fact]
    public void BuiltInProfileCatalog_HasNoCatalogDependency()
    {
        // WorkflowProfileCatalog.LoadProfile is intentionally Definition-only
        // (no runner is registered at startup); calling it here must not require
        // a catalog source.
        Assert.NotNull(WorkflowProfileCatalog.GetDefinition("mohist/local"));
        Assert.NotNull(WorkflowProfileCatalog.GetDefinition("mohist/github-pr"));
    }

    [Fact]
    public async Task RuntimeDeserialize_StaysDefinitionOnly_EvenWithCatalogPresent()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/missing",
                [new ActionCatalogInput("prompt", ["string"], true)], [], [])],
            []);

        // Build a Profile whose Definition is shaped around a declared Action,
        // but which a strict catalog check would reject because it supplies no
        // `with`. Persist it via WorkflowProfilePersistence.Serialize (which
        // runs WorkflowDefinitionValidator.Validate — Definition-only) and read
        // it back via Deserialize (also Definition-only). The catalog check
        // must not run on the read path; this is the runtime-load contract.
        var profile = new WorkflowProfile(
            "stored",
            "Stored",
            "",
            new WorkflowDefinition(
            [
                new StageDefinition("build",
                    [new TaskDefinition("t", "T", Uses: "mohist/missing")],
                    []),
            ]));
        var json = WorkflowProfilePersistence.Serialize(profile);
        var dbFactory = new TestDbContextFactory(_database.Options);
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = "proj-runtime",
                TemplateId = "stored",
                Template = json,
            });
            await db.SaveChangesAsync();
        }

        var projectManager = new ProjectWorkflowProfileManager(
            dbFactory, new StubActionCatalogSource(catalog));

        var stored = await projectManager.GetTemplateProfileAsync("proj-runtime", "stored");

        Assert.NotNull(stored);
        Assert.Equal("stored", stored!.Id);
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private ProjectWorkflowProfileManager BuildManagerWithCatalog(ActionCatalog catalog) =>
        new(new TestDbContextFactory(_database.Options),
            new StubActionCatalogSource(catalog));

    private static ActionCatalog SimpleCatalog() =>
        new([new ActionCatalogEntry("mohist/opencode", [], [], [])], []);

    private static string TemplateYaml(string id, string stageName = "stage-1") =>
        $"""
        id: {id}
        stages:
          - stage: {stageName}
            tasks: []
            checks: []
        """;

    private sealed class StubPromptLoader : IPromptLoader
    {
        public Dictionary<string, string> LoadAll() => new(StringComparer.Ordinal);
    }
}
