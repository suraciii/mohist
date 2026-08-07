using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Services;

public sealed class ProjectVariableStoreSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly ProjectVariableStore _store;

    public ProjectVariableStoreSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _store = new ProjectVariableStore(new TestDbContextFactory(_database.Options));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GetVariables_ReturnsEmpty_WhenNotSet()
    {
        var bundle = await _store.GetVariablesAsync("proj-none");

        Assert.Same(VariableBundle.Empty, bundle);
    }

    [Fact]
    public async Task SetVariables_StoresBundle()
    {
        var bundle = new VariableBundle(JsonSerializer.SerializeToElement(new { value = 1 }));

        var written = await _store.SetVariablesAsync("proj-set", bundle);
        var persisted = await _store.GetVariablesAsync("proj-set");

        Assert.Equal(written.ToJson(), persisted.ToJson());
    }

    [Fact]
    public async Task SetVariables_SanitizesRootAndStageAgentKeys()
    {
        var bundle = new VariableBundle(
            JsonSerializer.SerializeToElement(new
            {
                agent = new
                {
                    model = "gpt-5",
                    variant = "high",
                    runtime = "opencode",
                    type = "legacy",
                    livenessQuietThresholdMs = 1000,
                },
            }),
            new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.SerializeToElement(new
                {
                    agent = new
                    {
                        model = "claude-sonnet-4",
                        variant = "low",
                        runtime = "opencode",
                        type = "legacy",
                    },
                })),
            });

        var written = await _store.SetVariablesAsync("proj-sanitize", bundle);
        var persisted = await _store.GetVariablesAsync("proj-sanitize");

        AssertAgent(written.Vars, "gpt-5", "high");
        AssertStageAgent(written, "plan", "claude-sonnet-4", "low");
        AssertAgent(persisted.Vars, "gpt-5", "high");
        AssertStageAgent(persisted, "plan", "claude-sonnet-4", "low");
    }

    [Fact]
    public async Task PatchVariables_DeepMergesNestedFieldsAndUnknownStage()
    {
        var initial = new VariableBundle(
            JsonSerializer.SerializeToElement(new
            {
                agent = new { model = "gpt-5", variant = "low" },
                settings = new { keep = true },
            }),
            new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.SerializeToElement(new { existing = 1 })),
            });
        await _store.SetVariablesAsync("proj-patch", initial);

        var patch = new VariableBundle(
            JsonSerializer.SerializeToElement(new { agent = new { variant = "high" } }),
            new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { added = 2 })),
            });

        var merged = await _store.PatchVariablesAsync("proj-patch", patch);

        AssertAgent(merged.Vars, "gpt-5", "high");
        Assert.True(merged.Vars!.Value.GetProperty("settings").GetProperty("keep").GetBoolean());
        Assert.Equal(1, merged.Stages!["plan"].Vars!.Value.GetProperty("existing").GetInt32());
        Assert.Equal(2, merged.Stages["build"].Vars!.Value.GetProperty("added").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetVariables_RejectsNonObjectVarsWithoutPersisting(bool invalidStage)
    {
        var invalid = JsonSerializer.SerializeToElement(1);
        var bundle = invalidStage
            ? new VariableBundle(null, new Dictionary<string, StageVariables>
            {
                ["plan"] = new(invalid),
            })
            : new VariableBundle(invalid);
        var projectId = invalidStage ? "proj-invalid-stage" : "proj-invalid-root";

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetVariablesAsync(projectId, bundle));

        Assert.Same(VariableBundle.Empty, await _store.GetVariablesAsync(projectId));
    }

    private static void AssertStageAgent(VariableBundle bundle, string stage, string model, string variant)
    {
        Assert.NotNull(bundle.Stages);
        AssertAgent(bundle.Stages[stage].Vars, model, variant);
    }

    private static void AssertAgent(JsonElement? vars, string model, string variant)
    {
        Assert.True(vars.HasValue);
        var agent = vars.Value.GetProperty("agent");
        Assert.Equal(2, agent.EnumerateObject().Count());
        Assert.Equal(model, agent.GetProperty("model").GetString());
        Assert.Equal(variant, agent.GetProperty("variant").GetString());
    }
}
