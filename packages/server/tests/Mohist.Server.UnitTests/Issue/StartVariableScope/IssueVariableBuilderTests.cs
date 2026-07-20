using System.Text.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.StartVariableScope;

/// <summary>
/// Covers the T1 (issue-start) variable scope builder: it must produce the
/// issue's effective <c>Variables</c> by generically merging the global
/// <c>config.jsonc</c> bundle and the project <c>Variables</c> bundle
/// (project wins, global fills gaps, symmetric for <c>vars</c> and each
/// <c>stages.&lt;stage&gt;.vars</c>) and then layering the built-in calling
/// context (<c>mohist</c> / <c>issue</c> / <c>project</c> / <c>repository</c>
/// / <c>openspec*</c>) on top. The result is snapshotted once, at issue
/// creation, so subsequent edits to project or global <c>Variables</c> do
/// not retroactively change this issue's effective variables.
/// </summary>
public class IssueVariableBuilderTests
{
    private static readonly WorkflowProjectContext Project = new(
        Id: "proj_test",
        Name: "mohist-local",
        RepositoryName: "master",
        RepositoryGitUrl: null,
        RepositoryBaseBranch: "master");

    private static readonly WorkspaceIdentity Workspace = new(
        Path: "/tmp/mohist/test/issue-80",
        Branch: "mohist/run-wr_x",
        ChangeDir: "openspec/changes/issue-80");

    private static MohistIssue TestIssue(int number = 80) => new()
    {
        ProjectId = Project.Id,
        Number = number,
        Title = "Kanban 看板 Cancelled issues 可见性交互混乱",
        Body = "Test body",
        Priority = "p1",
    };

    private static VariableBundle BundleFrom(string json) =>
        VariableBundle.FromJson(json);

    [Fact]
    public void ProjectAgentConfig_PresentInVars_WhenProjectSetsAgent()
    {
        // The project profile sets vars.agent. The issue's T1-merged
        // variables carry it through unchanged.
        var globalBundle = VariableBundle.Empty;
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { model = "minimax-coding-plan/MiniMax-M3" }
            }
        }));

        var result = IssueVariableBuilder.Build(
            globalBundle, projectBundle, "wr_x", TestIssue(), Project, Workspace);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        // VariableBundle.Vars *is* the vars namespace — agent is a direct child.
        var agent = doc.RootElement.GetProperty("agent");

        Assert.False(agent.TryGetProperty("type", out _));
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
    }

    [Fact]
    public void ProjectAgentConfig_WinsOverGlobalAgent()
    {
        // Project sets model. Global also sets model. Project wins for the
        // overlapping key. Per D5, the converged surface no longer accepts
        // legacy `type`/`liveness*` keys, so non-overlapping legacy keys
        // from the global layer are dropped instead of being deep-merged.
        var globalBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { model = "openai/gpt-5.5", type = "openai-acp" }
            }
        }));
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { model = "minimax-coding-plan/MiniMax-M3" }
            }
        }));

        var result = IssueVariableBuilder.Build(
            globalBundle, projectBundle, "wr_x", TestIssue(), Project, Workspace);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        // Project wins for the overlapping key.
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
        // Legacy keys never enter vars.agent from either layer.
        Assert.False(agent.TryGetProperty("type", out _));
    }

    [Fact]
    public void GlobalAgentConfig_FillsGap_WhenProjectOmitsAgent()
    {
        // Project does not set vars.agent. Global config.jsonc supplies
        // vars.agent via ConfigService.GetVariables(); the issue's T1-merged
        // variables carry the global value.
        var globalBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { model = "openai/gpt-5.5" }
            }
        }));
        var projectBundle = VariableBundle.Empty;

        var result = IssueVariableBuilder.Build(
            globalBundle, projectBundle, "wr_x", TestIssue(), Project, Workspace);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.False(agent.TryGetProperty("type", out _));
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
    }

    [Fact]
    public void BuiltInContext_AlwaysPresent_EvenWhenNoGlobalOrProject()
    {
        var result = IssueVariableBuilder.Build(
            globalBundle: VariableBundle.Empty,
            projectBundle: VariableBundle.Empty,
            workflowRunId: "wr_x",
            issue: TestIssue(number: 80),
            project: Project,
            workspace: Workspace);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var root = doc.RootElement;

        Assert.Equal("mohist", root.GetProperty("mohist").GetProperty("system").GetString());
        Assert.Equal("wr_x", root.GetProperty("mohist").GetProperty("runId").GetString());
        Assert.Equal(80, root.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("proj_test", root.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal("issue-80", root.GetProperty("openspecChangeName").GetString());
        Assert.Equal("openspec/changes/issue-80", root.GetProperty("openspecChangeDir").GetString());
    }

    [Fact]
    public void BuiltInContextWinsOverProjectAndGlobal()
    {
        // Even if project or global vars try to override a context key
        // (mohist / issue / project / repository / openspec*), the built-in
        // context layered on top wins — the runtime reads these from the
        // issue profile as authoritative values.
        var globalBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                issue = new { number = 999 }
            }
        }));
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                project = new { id = "sneaky-override" }
            }
        }));

        var result = IssueVariableBuilder.Build(
            globalBundle, projectBundle, "wr_x", TestIssue(number: 80), Project, Workspace);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var root = doc.RootElement;

        Assert.Equal(80, root.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("proj_test", root.GetProperty("project").GetProperty("id").GetString());
    }

    [Fact]
    public void StageAgentConfig_ProjectOverridesGlobalSymmetrically()
    {
        // Per-stage merge uses the same project-over-global pattern as
        // top-level vars: project stage override wins; global stage override
        // fills gaps; both are merged via the same VariableBundle.MergeAll
        // path (no agent-special branch).
        var globalBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            stages = new Dictionary<string, object>
            {
                ["build"] = new
                {
                    vars = new
                    {
                        agent = new { model = "openai/gpt-5.5" }
                    }
                }
            }
        }));
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            stages = new Dictionary<string, object>
            {
                ["build"] = new
                {
                    vars = new
                    {
                        agent = new { model = "minimax-coding-plan/MiniMax-M3" }
                    }
                }
            }
        }));

        var result = IssueVariableBuilder.Build(
            globalBundle, projectBundle, "wr_x", TestIssue(), Project, Workspace);

        // Per-stage variables live in VariableBundle.Stages, not Vars.
        Assert.NotNull(result.Stages);
        Assert.True(result.Stages!.TryGetValue("build", out var buildStage));
        Assert.NotNull(buildStage.Vars);

        using var doc = JsonDocument.Parse(buildStage.Vars.Value.GetRawText());
        var buildAgent = doc.RootElement.GetProperty("agent");

        // Project wins for the overlapping key.
        Assert.Equal("minimax-coding-plan/MiniMax-M3", buildAgent.GetProperty("model").GetString());
    }

    [Fact]
    public void ProjectUserVariables_DeepMergedWithGlobal()
    {
        // Global sets vars.agent with model + variant. Project adds
        // vars.customProjectKey. Final vars retains both (deep merge across
        // the bundle), and the merged vars.agent is projected down to the
        // converged {model, variant} whitelist so legacy ACP/liveness keys
        // never enter the merged result regardless of source.
        var globalBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { model = "openai/gpt-5.5", variant = "max", livenessQuietThresholdMs = 1200000 }
            }
        }));
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                customProjectKey = "kept"
            }
        }));

        var result = IssueVariableBuilder.Build(
            globalBundle, projectBundle, "wr_x", TestIssue(), Project, Workspace);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal("max", agent.GetProperty("variant").GetString());
        Assert.False(agent.TryGetProperty("livenessQuietThresholdMs", out _));
        Assert.False(agent.TryGetProperty("type", out _));

        // Non-agent top-level keys from project are preserved across the merge.
        Assert.Equal("kept", doc.RootElement.GetProperty("customProjectKey").GetString());
    }

    [Fact]
    public void VarsAndStages_UseIdenticalMergePattern()
    {
        // The acceptance criterion for "symmetric": top-level `vars` and
        // each `stages.<stage>.vars` use the same project-over-global
        // precedence via VariableBundle.MergeAll. This test exercises both
        // with the same shape of input to assert the symmetry.
        var globalBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new { shared = "global-vars" },
            stages = new Dictionary<string, object>
            {
                ["build"] = new { vars = new { shared = "global-stages" } }
            }
        }));
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new { shared = "project-vars" },
            stages = new Dictionary<string, object>
            {
                ["build"] = new { vars = new { shared = "project-stages" } }
            }
        }));

        var result = IssueVariableBuilder.Build(
            globalBundle, projectBundle, "wr_x", TestIssue(), Project, Workspace);

        // top-level vars
        Assert.NotNull(result.Vars);
        using var varsDoc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.Equal("project-vars", varsDoc.RootElement.GetProperty("shared").GetString());

        // per-stage vars: same precedence, same MergeAll path
        Assert.NotNull(result.Stages);
        Assert.True(result.Stages!.TryGetValue("build", out var buildStage));
        Assert.NotNull(buildStage.Vars);
        using var stagesDoc = JsonDocument.Parse(buildStage.Vars.Value.GetRawText());
        Assert.Equal("project-stages", stagesDoc.RootElement.GetProperty("shared").GetString());
    }
}
