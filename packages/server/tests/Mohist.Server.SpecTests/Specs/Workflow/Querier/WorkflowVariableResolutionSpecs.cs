using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

public class WorkflowVariableResolutionSpecs : WorkflowProfileManagerTestFactory
{
    [Fact]
    public async Task ResolveLayeredVariables_ReturnsEmpty_WhenNoProfileVariablesExist()
    {
        var runId = "wr_vars01";
        await SeedAsync(
            projectId: "proj5",
            issueNumber: 1,
            runId: runId,
            issueTemplateJson: SerializeDefinition("empty-vars-template"));

        var result = await Resolver.ResolveConfiguredVariablesAsync(runId);

        Assert.False(result.Vars.HasValue);
        Assert.Null(result.Stages);
    }

    [Fact]
    public async Task ResolveLayeredVariables_MergesProjectDefaultsWithIssueOverrides()
    {
        var runId = "wr_direct01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { a = 1, b = "proj-b", c = "proj-c" })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { b = "issue-b", c = "issue-c", d = "issue-d" })));

        await SeedAllLayersAsync("proj6", 1, runId, proj, issue);

        var result = await Resolver.ResolveConfiguredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
        Assert.Equal("issue-b", doc.RootElement.GetProperty("b").GetString());
        Assert.Equal("issue-c", doc.RootElement.GetProperty("c").GetString());
        Assert.Equal("issue-d", doc.RootElement.GetProperty("d").GetString());
    }

    [Fact]
    public async Task ResolveLayeredVariables_ProjectEditsAffectExistingIssueWhenNotOverridden()
    {
        // Project-level workflow variables are live defaults. Existing issues
        // inherit updated project model settings unless the issue profile
        // explicitly overrides the same leaf.
        var runId = "wr_snapshot01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "minimax-coding-plan/MiniMax-M3" } })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { issueContext = true })));

        await SeedAllLayersAsync("proj_snap", 1, runId, proj, issue);

        await using (var db = new MohistDbContext(Database.Options))
        {
            var row = db.ProjectWorkflowProfiles.Single(x => x.ProjectId == "proj_snap");
            row.Variables = new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                { agent = new { model = "anthropic/claude-sonnet-4-6" } }))
            ).ToJson();
            await db.SaveChangesAsync();
        }

        var result = await Resolver.ResolveConfiguredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.True(doc.RootElement.GetProperty("issueContext").GetBoolean());
        Assert.Equal("anthropic/claude-sonnet-4-6",
            doc.RootElement.GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveLayeredVariables_IssueModelOverrideWinsOverProjectModel()
    {
        var runId = "wr_override01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { type = "opencode", model = "project/default" } })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "issue/override" } })));

        await SeedAllLayersAsync("proj_override", 1, runId, proj, issue);

        var result = await Resolver.ResolveConfiguredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("issue/override", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ProjectStageModelAppliesWhenIssueOnlyHasContext()
    {
        var runId = "wr_project_stage01";
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { type = "opencode", model = "project/default" }
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["check"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new
                    {
                        agent = new { type = "opencode", model = "openai/gpt-5.5" }
                    })))
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                issue = new { number = 122 }
            })));

        await SeedAllLayersAsync("proj_project_stage", 1, runId, project, issue);

        var result = await Resolver.ResolveEffectiveVariablesAsync(runId, "check");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        var agent = result.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal(122, result.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.False(result.TryGetProperty("mohist", out _));
        Assert.False(result.TryGetProperty("project", out _));
        Assert.False(result.TryGetProperty("workspace", out _));
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ReadsIssueStageAndFallsBackToTopLevel()
    {
        // Per-stage variables are read directly from the issue's Stages
        // map. Runtime dispatch falls back from
        // Variables.stages[stage].vars.agent to Variables.vars.agent via
        // ordinary variable lookups — no cross-layer resolution.
        var runId = "wr_stage01";
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "minimax-coding-plan/MiniMax-M3" }
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new
                    {
                        agent = new { model = "anthropic/claude-sonnet-4-6" }
                    })))
            });

        await SeedIssueOnlyAsync("proj_stage", 1, runId, issue);

        var result = await Resolver.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal("anthropic/claude-sonnet-4-6",
            result.GetProperty("agent").GetProperty("model").GetString());

        var topLevelResult = await Resolver.ResolveEffectiveVariablesAsync(runId, null);
        Assert.Equal("minimax-coding-plan/MiniMax-M3",
            topLevelResult.GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_UsesWorkflowWideVarsWhenStageHasNoVars()
    {
        var runId = "wr_stage_missing01";
        var issue = new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                agent = new { model = "workflow-wide-model" },
                shared = "workflow-wide",
            }));

        await SeedIssueOnlyAsync("proj_stage_missing", 1, runId, issue);

        var topLevelResult = await Resolver.ResolveEffectiveVariablesAsync(runId, null);
        var stageResult = await Resolver.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal(topLevelResult.GetRawText(), stageResult.GetRawText());
    }

    [Fact]
    public async Task LoadIssueWorkspace_ReturnsStableIdentityFromIssueVariables()
    {
        var runId = "wr_workspace01";
        var issue = new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                workspace = new
                {
                    path = "/workspaces/issue-1",
                    branch = "issue/1",
                    changeDir = "packages/server",
                },
            }));

        await SeedIssueOnlyAsync("proj_workspace", 1, runId, issue);

        var first = await Resolver.LoadIssueWorkspaceAsync("proj_workspace", 1);
        var second = await Resolver.LoadIssueWorkspaceAsync("proj_workspace", 1);

        var expected = new WorkspaceIdentity(
            "/workspaces/issue-1",
            "issue/1",
            "packages/server");
        Assert.Equal(expected, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ResolveLayeredVariables_MergesTemplateProjectIssueAndRuntimeLayers()
    {
        // issue-474 T-002: Workflow Definitions no longer carry embedded
        // variables. The template fixture is now a pure Definition shape;
        // effective values come from Project, Issue, and Run VariableBundles.
        var runId = "wr_effective01";
        var templateJson = SerializeDefinition("effective-template");
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                source = "project",
                agent = new { model = "project-model" },
            })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                source = "issue",
                agent = new { type = "issue-agent" },
            })));
        var runtime = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                source = "runtime",
                github = new { pr = new { number = 249, url = "https://example.test/pr/249" } },
            })));

        await SeedAllLayersAsync("proj_effective", 1, runId, project, issue,
            issueTemplateJson: templateJson,
            runtime: runtime);

        var result = await Resolver.ResolveConfiguredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;
        Assert.Equal("runtime", root.GetProperty("source").GetString());
        Assert.Equal("project-model", root.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("issue-agent", root.GetProperty("agent").GetProperty("type").GetString());
        var pr = root.GetProperty("github").GetProperty("pr");
        Assert.Equal(249, pr.GetProperty("number").GetInt32());
        Assert.Equal("https://example.test/pr/249", pr.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ReturnsRunnerVarsForStage()
    {
        // issue-474 T-002: templates are now pure Definition assets and do
        // not contribute variables. The selected-stage overlay is read from
        // Project/Issue/Run VariableBundles only.
        var runId = "wr_effective_stage01";
        var templateJson = SerializeDefinition("effective-stage-template");
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "project-model" },
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    agent = new { type = "opencode", model = "build-model" },
                    stageOnly = true,
                })))
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                issueOnly = true,
            })));

        await SeedAllLayersAsync("proj_effective_stage", 1, runId, project, issue,
            issueTemplateJson: templateJson);

        var result = await Resolver.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.False(result.TryGetProperty("vars", out _));
        Assert.False(result.TryGetProperty("stages", out _));
        Assert.False(result.TryGetProperty("mohist", out _));
        Assert.False(result.TryGetProperty("project", out _));
        Assert.False(result.TryGetProperty("workspace", out _));
        Assert.True(result.GetProperty("stageOnly").GetBoolean());
        Assert.True(result.GetProperty("issueOnly").GetBoolean());
        var agent = result.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("build-model", agent.GetProperty("model").GetString());
    }

}
