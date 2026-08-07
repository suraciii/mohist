using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

public sealed class WorkflowVariableResolutionThreeScopesSpecs : WorkflowDefinitionResolverTestFactory
{
    [Fact]
    public async Task EffectiveVariables_MergeProjectIssueAndRunWithStagePrecedence()
    {
        var runId = "wr_three_scopes01";
        var project = new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                agent = new { model = "project-model", variant = "project-variant" },
                shared = "project",
            }),
            Stages: Stage("build", new
            {
                agent = new { variant = "project-stage-variant" },
                stageShared = "project-stage",
            }));
        var issue = new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                agent = new { model = "issue-model" },
                shared = "issue",
            }),
            Stages: Stage("build", new
            {
                agent = new { model = "issue-stage-model" },
            }));
        var run = new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new { shared = "run" }),
            Stages: Stage("build", new { stageShared = "run-stage" }));
        await SeedAllLayersAsync(
            "proj_three_scopes", 1, runId,
            project: project,
            issue: issue,
            runtime: run);

        var topResult = await Resolver.ResolveEffectiveVariablesAsync(runId, null);
        var stageResult = await Resolver.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal("run", topResult.GetProperty("shared").GetString());
        Assert.Equal("issue-model", topResult.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("project-variant", topResult.GetProperty("agent").GetProperty("variant").GetString());
        Assert.Equal("run", stageResult.GetProperty("shared").GetString());
        Assert.Equal("run-stage", stageResult.GetProperty("stageShared").GetString());
        Assert.Equal("issue-stage-model", stageResult.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("project-stage-variant", stageResult.GetProperty("agent").GetProperty("variant").GetString());
    }

    [Fact]
    public async Task EffectiveVariables_FreshRunHasNoArchiveKey()
    {
        var runId = "wr_three_scopes02";
        await SeedAllLayersAsync(
            "proj_three_scopes", 2, runId,
            project: VariableBundle.Empty,
            issue: VariableBundle.Empty);

        var result = await Resolver.ResolveEffectiveVariablesAsync(runId, null);

        Assert.False(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("archive", out _));
    }

    [Fact]
    public async Task ExplicitRunWriteUsesStandardPrecedence()
    {
        var runId = "wr_three_scopes03";
        await SeedAllLayersAsync(
            "proj_three_scopes", 3, runId,
            project: Bundle(new { archive = "/project/archive" }),
            issue: VariableBundle.Empty);
        var store = new WorkflowRunVariablesStore(new TestDbContextFactory(Database.Options));

        await store.SetVariablesAsync(runId, Bundle(new { archive = "/run/archive" }));
        var result = await Resolver.ResolveEffectiveVariablesAsync(runId, null);

        Assert.Equal("/run/archive", result.GetProperty("archive").GetString());
    }

    [Fact]
    public async Task WorkflowRunProfile_RejectsOverlappingWrites()
    {
        var runId = "wr_three_scopes04";
        var store = new WorkflowRunVariablesStore(new TestDbContextFactory(Database.Options));
        await store.SetVariablesAsync(runId, Bundle(new { value = "initial" }));
        await using var readerA = new MohistDbContext(Database.Options);
        await using var readerB = new MohistDbContext(Database.Options);
        var rowA = await readerA.WorkflowRunProfiles.FirstAsync(x => x.WorkflowRunId == runId);
        var rowB = await readerB.WorkflowRunProfiles.FirstAsync(x => x.WorkflowRunId == runId);

        rowA.Variables = Bundle(new { value = "first" }).ToJson();
        BumpETag(readerA, rowA);
        await readerA.SaveChangesAsync();
        rowB.Variables = Bundle(new { value = "second" }).ToJson();
        BumpETag(readerB, rowB);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => readerB.SaveChangesAsync());
    }

    private static VariableBundle Bundle<T>(T value) =>
        new(JsonSerializer.SerializeToElement(value));

    private static Dictionary<string, StageVariables> Stage<T>(string stage, T value) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [stage] = new StageVariables(JsonSerializer.SerializeToElement(value)),
        };

    private static void BumpETag(MohistDbContext db, WorkflowRunProfileRow row)
    {
        var etag = db.Entry(row).Property<long>("ETag");
        etag.CurrentValue = etag.OriginalValue + 1;
    }
}
