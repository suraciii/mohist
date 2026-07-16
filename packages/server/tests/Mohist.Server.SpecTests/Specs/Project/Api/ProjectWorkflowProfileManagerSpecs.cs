using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Project.Api;

public class ProjectWorkflowProfileManagerSpecs : IAsyncLifetime
{
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly ProjectWorkflowProfileManager _manager;
    private readonly SqliteConnection _keeper;

    public ProjectWorkflowProfileManagerSpecs()
    {
        var connectionString = $"Data Source=proj-profile-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        _manager = new ProjectWorkflowProfileManager(new Factory(_options), new StubPromptLoader(), new PromptTemplateEngine());

        MigratedSqliteTemplate.CopyModelSchemaTo(_keeper);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
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
        var info = await _manager.CreateTemplateAsync("proj-create", MinimalYaml("my-template"));

        Assert.Equal("proj-create", info.ProjectId);
        Assert.Equal("my-template", info.TemplateId);

        var def = await _manager.GetTemplateAsync("proj-create", "my-template");
        Assert.NotNull(def);
        Assert.Equal("my-template", def.Id);
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

    // ===================== Variables Set/Patch =====================

    [Fact]
    public async Task GetVariables_ReturnsEmpty_WhenNotSet()
    {
        var bundle = await _manager.GetVariablesAsync("proj-none");
        Assert.Same(VariableBundle.Empty, bundle);
    }

    [Fact]
    public async Task SetVariables_StoresBundle()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { a = 1 })));

        await _manager.SetVariablesAsync("proj-set-vars", bundle);
        var result = await _manager.GetVariablesAsync("proj-set-vars");

        Assert.NotNull(result.Vars);
    }

    [Fact]
    public async Task PatchVariables_DeepMerges_WithExisting()
    {
        var initial = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
                new { agent = new { type = "opencode", model = "sonnet-4" } })));
        await _manager.SetVariablesAsync("proj-patch", initial);

        var patch = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
                new { agent = new { model = "gpt-4o" } })));
        await _manager.PatchVariablesAsync("proj-patch", patch);

        var result = await _manager.GetVariablesAsync("proj-patch");
        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
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

    private class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;
        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;
        public MohistDbContext CreateDbContext() => new(_options);
    }

    private sealed class StubPromptLoader : IPromptLoader
    {
        public Dictionary<string, string> LoadAll() => new(StringComparer.Ordinal);
    }
}
