using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Project.Services;

public class ProjectWorkflowProfileManagerSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly ProjectWorkflowProfileManager _manager;

    public ProjectWorkflowProfileManagerSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _manager = new ProjectWorkflowProfileManager(new TestDbContextFactory(_database.Options), NullActionCatalogSource.Instance);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    // ===================== System templates =====================

    // ===================== Project templates CRUD =====================

    [Fact]
    public async Task ListTemplates_EmptyProject()
    {
        var list = await _manager.ListTemplatesAsync("proj-empty");

        Assert.Empty(list);
    }

    [Fact]
    public async Task CreateTemplate_ParsesYamlAndStores()
    {
        var result = await _manager.CreateTemplateAsync("proj-create", MinimalYaml("my-template"));

        Assert.Equal("proj-create", result.Template.ProjectId);
        Assert.Equal("my-template", result.Template.TemplateId);
        Assert.Equal(ActionValidationStatus.Skipped, result.ActionValidation);

        var profile = await _manager.GetTemplateProfileAsync("proj-create", "my-template");
        Assert.NotNull(profile);
        Assert.Equal("my-template", profile.Id);
        Assert.Equal("my-template", profile.Name);
    }

    [Fact]
    public async Task CreateTemplate_DuplicateId_Throws()
    {
        await _manager.CreateTemplateAsync("proj-dup", MinimalYaml("t1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.CreateTemplateAsync("proj-dup", MinimalYaml("t1")));
    }

    [Fact]
    public async Task UpdateTemplate_ReplacesDefinition()
    {
        await _manager.CreateTemplateAsync("proj-update", MinimalYaml("t1", stageName: "stage-1"));

        var info = await _manager.UpdateTemplateAsync("proj-update", "t1",
            MinimalYaml("t1", stageName: "stage-2"));

        Assert.NotNull(info);
        var def = await _manager.GetTemplateAsync("proj-update", "t1");
        Assert.Contains(def!.Stages, s => s.Stage == "stage-2");
    }

    [Fact]
    public async Task UpdateTemplate_IdMismatch_Throws()
    {
        await _manager.CreateTemplateAsync("proj-mismatch", MinimalYaml("t1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpdateTemplateAsync("proj-mismatch", "t1", MinimalYaml("t2")));
    }

    [Fact]
    public async Task UpdateTemplate_NonExistent_ReturnsNull()
    {
        var info = await _manager.UpdateTemplateAsync("proj-no", "t1", MinimalYaml("t1"));
        Assert.Null(info);
    }

    [Fact]
    public async Task DeleteTemplate_RemovesRow()
    {
        await _manager.CreateTemplateAsync("proj-del", MinimalYaml("t1"));

        Assert.True(await _manager.DeleteTemplateAsync("proj-del", "t1"));
        Assert.False(await _manager.DeleteTemplateAsync("proj-del", "t1"));
    }

    [Fact]
    public async Task GetTemplateProfile_TamperedStoredDefinitionSurfacesDefinitionError()
    {
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = "proj-tampered",
                TemplateId = "tampered",
                Template = """
                    {"id":"tampered","name":"Tampered","description":"","definition":{"stages":[]}}
                    """,
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            _manager.GetTemplateProfileAsync("proj-tampered", "tampered"));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("stages", error.Path);
        Assert.Equal(ValidationSource.Definition, error.Source);
    }

    [Fact]
    public async Task GetTemplateProfile_NullStoredDefinitionSurfacesDefinitionError()
    {
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = "proj-null-definition",
                TemplateId = "null-definition",
                Template = "{\"id\":\"null-definition\",\"name\":\"Null\",\"description\":\"\",\"definition\":null}",
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            _manager.GetTemplateProfileAsync("proj-null-definition", "null-definition"));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("", error.Path);
        Assert.Equal("definition is required", error.Message);
        Assert.Equal(ValidationSource.Definition, error.Source);
    }

    [Fact]
    public async Task CreateTemplate_InlineAgentGuardReturnsActionError()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode", [], [], [])],
            []);
        var managerWithCatalog = new ProjectWorkflowProfileManager(
            new TestDbContextFactory(_database.Options),
            new StubActionCatalogSource(catalog));

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            managerWithCatalog.CreateTemplateAsync("proj-action-error", """
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
    }

    [Fact]
    public async Task CreateTemplate_MultipleYamlDocumentsReturnsDefinitionError()
    {
        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            _manager.CreateTemplateAsync("proj-multi-document", """
                stages:
                  - stage: build
                    tasks: []
                    checks: []
                ---
                unknown: true
                """));

        var error = Assert.Single(exception.Errors);
        Assert.Equal("", error.Path);
        Assert.Equal("yaml must contain exactly one document", error.Message);
        Assert.Equal(ValidationSource.Definition, error.Source);
    }

    [Fact]
    public async Task DefinitionMigrationMapsLegacyCheckNameToId()
    {
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = "proj-migrate",
                TemplateId = "legacy",
                Template = """
                    {"id":"legacy","name":"Legacy","description":"","definition":{"stages":[{"stage":"build","tasks":[],"checks":[{"name":"lint","title":"Lint","uses":"mohist/check"}]}]}}
                    """,
            });
            await db.SaveChangesAsync();
            await WorkflowProfileDataUpgrader.UpgradeAsync(db);
        }

        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.ProjectWorkflowTemplates.SingleAsync(x => x.ProjectId == "proj-migrate");
            Assert.Contains("\"id\":\"lint\"", row.Template, StringComparison.Ordinal);
            Assert.DoesNotContain("\"name\":\"lint\"", row.Template, StringComparison.Ordinal);
        }
    }

    // ===================== Default template =====================

    [Fact]
    public async Task SetDefaultTemplate_RequiresTemplateToExist()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.SetDefaultTemplateAsync("proj-nowhere", "ghost"));
    }

    [Fact]
    public async Task SetDefaultTemplate_AcceptsProjectTemplate()
    {
        await _manager.CreateTemplateAsync("proj-set", MinimalYaml("t1"));
        var result = await _manager.SetDefaultTemplateAsync("proj-set", "t1");
        Assert.Equal("t1", result);
        Assert.Equal("t1", await _manager.GetDefaultTemplateAsync("proj-set"));
    }

    [Fact]
    public async Task SetDefaultTemplate_AcceptsSystemTemplate()
    {
        var result = await _manager.SetDefaultTemplateAsync("proj-sys", "mohist/local");
        Assert.Equal("mohist/local", result);
    }

    [Fact]
    public async Task SetDefaultTemplate_NullClears()
    {
        await _manager.CreateTemplateAsync("proj-clear", MinimalYaml("t1"));
        await _manager.SetDefaultTemplateAsync("proj-clear", "t1");
        await _manager.SetDefaultTemplateAsync("proj-clear", null);
        Assert.Null(await _manager.GetDefaultTemplateAsync("proj-clear"));
    }

    // ===================== helpers =====================

    private static string MinimalYaml(string id, string stageName = "stage-1") =>
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
