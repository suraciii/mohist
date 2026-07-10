using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Specs.Workflow.Querier;

public class WorkflowProfileVariablesSpecs : IAsyncLifetime
{
    private readonly WorkflowProfileManagerTestContext _test = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _test.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ResolveLayeredVariables_ReturnsEmpty_WhenNoProfileVariablesExist()
    {
        var runId = "wr_vars01";
        await _test.SeedAsync(
            projectId: "proj5",
            issueId: "issue_5",
            runId: runId,
            issueTemplateJson: _test.SerializeDefinition("empty-vars-template"));

        var result = await _test.Manager.ResolveLayeredVariablesAsync(runId);

        Assert.False(result.Vars.HasValue);
        Assert.Null(result.Stages);
    }

    [Fact]
    public async Task ResolveLayeredVariables_MergesProjectDefaultsWithIssueOverrides()
    {
        var runId = "wr_direct01";
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { a = 1, b = "proj-b", c = "proj-c" })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { b = "issue-b", c = "issue-c", d = "issue-d" })));

        await _test.SeedAllLayersAsync("proj6", "issue_6", runId, project, issue);

        var result = await _test.Manager.ResolveLayeredVariablesAsync(runId);

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
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "minimax-coding-plan/MiniMax-M3" } })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { issueContext = true })));

        await _test.SeedAllLayersAsync("proj_snap", "issue_snap", runId, project, issue);

        await using (var db = new MohistDbContext(_test.Options))
        {
            var row = db.ProjectWorkflowProfiles.Single(x => x.ProjectId == "proj_snap");
            row.Variables = new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                { agent = new { model = "anthropic/claude-sonnet-4-6" } }))
            ).ToJson();
            await db.SaveChangesAsync();
        }

        var result = await _test.Manager.ResolveLayeredVariablesAsync(runId);

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
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { type = "opencode", model = "project/default" } })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "issue/override" } })));

        await _test.SeedAllLayersAsync("proj_override", "issue_override", runId, project, issue);

        var result = await _test.Manager.ResolveLayeredVariablesAsync(runId);

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
                agent = new { type = "opencode", model = "project/default" },
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["check"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new
                    {
                        agent = new { type = "opencode", model = "openai/gpt-5.5" },
                    }))),
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                issue = new { number = 122 },
            })));

        await _test.SeedAllLayersAsync("proj_project_stage", "issue_project_stage", runId, project, issue);

        var result = await _test.Manager.ResolveEffectiveVariablesAsync(runId, "check");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        var agent = result.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal(122, result.GetProperty("issue").GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ReadsIssueStageAndFallsBackToTopLevel()
    {
        // Per-stage variables are read directly from the issue's Stages map.
        // Runtime dispatch falls back from a stage value to the top-level
        // value through ordinary variable lookups, not cross-layer merging.
        var runId = "wr_stage01";
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "minimax-coding-plan/MiniMax-M3" },
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new
                    {
                        agent = new { model = "anthropic/claude-sonnet-4-6" },
                    }))),
            });

        await _test.SeedIssueOnlyAsync("proj_stage", "issue_stage", runId, issue);

        var result = await _test.Manager.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal("anthropic/claude-sonnet-4-6",
            result.GetProperty("agent").GetProperty("model").GetString());

        var topLevelResult = await _test.Manager.ResolveEffectiveVariablesAsync(runId, null);
        Assert.Equal("minimax-coding-plan/MiniMax-M3",
            topLevelResult.GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveLayeredVariables_MergesTemplateProjectIssueAndRuntimeLayers()
    {
        var runId = "wr_effective01";
        var templateJson = _test.SerializeDefinition(
            "effective-template",
            variables: new Dictionary<string, JsonElement?>
            {
                ["source"] = JsonSerializer.SerializeToElement("template"),
                ["agent"] = JsonSerializer.SerializeToElement(new { model = "template-model", type = "template-agent" }),
                ["github"] = JsonSerializer.SerializeToElement(new { pr = new { number = 1 } }),
            });
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
            })));
        var runtime = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                source = "runtime",
                github = new { pr = new { number = 249, url = "https://example.test/pr/249" } },
            })));

        await _test.SeedAllLayersAsync("proj_effective", "issue_effective", runId, project, issue,
            issueTemplateJson: templateJson,
            runtime: runtime);

        var result = await _test.Manager.ResolveLayeredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;
        Assert.Equal("runtime", root.GetProperty("source").GetString());
        Assert.Equal("project-model", root.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("template-agent", root.GetProperty("agent").GetProperty("type").GetString());
        var pr = root.GetProperty("github").GetProperty("pr");
        Assert.Equal(249, pr.GetProperty("number").GetInt32());
        Assert.Equal("https://example.test/pr/249", pr.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ReturnsRunnerVarsForStage()
    {
        var runId = "wr_effective_stage01";
        var templateJson = _test.SerializeDefinition(
            "effective-stage-template",
            variables: new Dictionary<string, JsonElement?>
            {
                ["agent"] = JsonSerializer.SerializeToElement(new { type = "opencode", model = "template-model" }),
            });
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "project-model" },
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    agent = new { model = "build-model" },
                    stageOnly = true,
                }))),
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                issueOnly = true,
            })));

        await _test.SeedAllLayersAsync("proj_effective_stage", "issue_effective_stage", runId, project, issue,
            issueTemplateJson: templateJson);

        var result = await _test.Manager.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.False(result.TryGetProperty("vars", out _));
        Assert.False(result.TryGetProperty("stages", out _));
        Assert.True(result.GetProperty("stageOnly").GetBoolean());
        Assert.True(result.GetProperty("issueOnly").GetBoolean());
        var agent = result.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("build-model", agent.GetProperty("model").GetString());
    }

    [Fact]
    public void ExpandTaskWith_NullTaskWith_ReturnsNull()
    {
        var result = WorkflowProfileManager.ExpandTaskWith(VariableBundle.Empty, null);

        Assert.Null(result);
    }

    [Fact]
    public void ExpandTaskWith_ResolvesWholeTemplateStringToJsonValue()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { type = "opencode", model = "sonnet-4" },
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["agent"] = JsonSerializer.SerializeToElement("${{ agent }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Object, result["agent"]!.Value.ValueKind);
        Assert.Equal("opencode", result["agent"]!.Value.GetProperty("type").GetString());
        Assert.Equal("sonnet-4", result["agent"]!.Value.GetProperty("model").GetString());
    }

    [Fact]
    public void ExpandTaskWith_DeepMergesObjectKey()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "gpt-4o", timeoutMs = 300000 },
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["agent"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { type = "opencode", timeoutMs = 600000 })),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result["agent"]));
        Assert.Equal("opencode", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("gpt-4o", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(300000, doc.RootElement.GetProperty("timeoutMs").GetInt32());
    }

    [Fact]
    public void ExpandTaskWith_PreservesPlainValues()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { other = 1 })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["count"] = JsonSerializer.SerializeToElement(42),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("task-1", result!["name"]!.Value.GetString());
        Assert.Equal(42, result!["count"]!.Value.GetInt32());
    }

    [Fact]
    public void ExpandTaskWith_ResolvesNestedWholeTemplatePath()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                config = new { deep = new { value = "found-it" } },
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["x"] = JsonSerializer.SerializeToElement("${{ config.deep.value }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("found-it", result!["x"]!.Value.GetString());
    }

    [Fact]
    public void ExpandTaskWith_ResolvesVarsPrefixedNestedWholeTemplatePath()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                github = new { pr = new { number = 42, url = "https://github.com/example/repo/pull/42" } },
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["prNumber"] = JsonSerializer.SerializeToElement("${{ vars.github.pr.number }}"),
            ["prUrl"] = JsonSerializer.SerializeToElement("${{ vars.github.pr.url }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal(42, result!["prNumber"]!.Value.GetInt32());
        Assert.Equal("https://github.com/example/repo/pull/42", result!["prUrl"]!.Value.GetString());
    }

    [Fact]
    public void ExpandTaskWith_PreservesUnresolvedWholeTemplateString()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { other = 1 })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["agent"] = JsonSerializer.SerializeToElement("${{ missing.agent }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("${{ missing.agent }}", result!["agent"]!.Value.GetString());
    }
}
