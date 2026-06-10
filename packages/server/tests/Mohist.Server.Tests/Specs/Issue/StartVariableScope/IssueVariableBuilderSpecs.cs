using System.Text.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Domain;
using Xunit;
using Mohist.Server.Tests.Support;
using MohistIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.Specs.Issue.StartVariableScope;

/// <summary>
/// Covers the issue-start variable scope builder: it must compose the issue-level
/// variables on top of the project-level variables so a project-wide agent model
/// (e.g. <c>vars.agent.model = "minimax-coding-plan/MiniMax-M3"</c>) reaches the
/// workflow dispatch layer. Regression coverage for the #80 dispatch stall where
/// <c>IssueGrain.BuildIssueVariables</c> discarded the project layer entirely.
/// </summary>
public class IssueVariableBuilderSpecs
{
    private static readonly WorkflowProjectContext Project = new(
        Id: "proj_test",
        Name: "mohist-local",
        Path: "/tmp/mohist-test",
        BaseBranch: "master",
        RepositoryName: "master",
        RepositoryRemote: null,
        RepositoryPath: "/tmp/mohist-test",
        RepositoryBaseBranch: "master");

    private static MohistIssue TestIssue(int number = 80) => new()
    {
        Id = $"issue_{number:D2}",
        ProjectId = Project.Id,
        Number = number,
        Title = "Kanban 看板 Cancelled issues 可见性交互混乱",
        Body = "Test body",
        Priority = "p1",
    };

    private static VariableBundle BundleFrom(string json) =>
        VariableBundle.FromJson(json);

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ProjectAgentConfig_PresentInVars_WhenIssueStarts()
    {
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { type = "opencode", model = "minimax-coding-plan/MiniMax-M3" }
            }
        }));
        var issueBundle = VariableBundle.Empty;

        var result = IssueVariableBuilder.Build(
            projectBundle, issueBundle, "wr_x", TestIssue(), Project);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        // VariableBundle.Vars *is* the vars namespace — agent is a direct child.
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void IssueAgentConfig_OverridesProject_WhenIssueStarts()
    {
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { type = "opencode", model = "minimax-coding-plan/MiniMax-M3" }
            }
        }));
        var issueBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { model = "openai/gpt-5.5" }
            }
        }));

        var result = IssueVariableBuilder.Build(
            projectBundle, issueBundle, "wr_x", TestIssue(), Project);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        // Issue-level model wins, project-level type still surfaces.
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void BuiltInContext_AlwaysPresent_EvenWhenNoProjectOrIssueConfig()
    {
        var result = IssueVariableBuilder.Build(
            projectBundle: VariableBundle.Empty,
            issueBundle: VariableBundle.Empty,
            workflowRunId: "wr_x",
            issue: TestIssue(number: 80),
            project: Project);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var root = doc.RootElement;

        Assert.Equal("mohist", root.GetProperty("mohist").GetProperty("system").GetString());
        Assert.Equal("wr_x", root.GetProperty("mohist").GetProperty("runId").GetString());
        Assert.Equal(80, root.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("proj_test", root.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal("issue-80", root.GetProperty("openspecChangeName").GetString());
        Assert.Equal("openspec/changes/issue-80", root.GetProperty("openspecChangeDir").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void StageAgentConfig_OverridesProjectStageAgentConfig()
    {
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { type = "opencode", model = "minimax-coding-plan/MiniMax-M3" }
            },
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
        var issueBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            stages = new Dictionary<string, object>
            {
                ["build"] = new
                {
                    vars = new
                    {
                        agent = new { model = "anthropic/claude-sonnet-4-6" }
                    }
                }
            }
        }));

        var result = IssueVariableBuilder.Build(
            projectBundle, issueBundle, "wr_x", TestIssue(), Project);

        // Per-stage variables live in VariableBundle.Stages, not Vars.
        Assert.NotNull(result.Stages);
        Assert.True(result.Stages!.TryGetValue("build", out var buildStage));
        Assert.NotNull(buildStage.Vars);

        using var doc = JsonDocument.Parse(buildStage.Vars.Value.GetRawText());
        var buildAgent = doc.RootElement.GetProperty("agent");

        // Issue-level stage override wins over project-level stage override.
        Assert.Equal("anthropic/claude-sonnet-4-6", buildAgent.GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void IssueUserVariables_DeepMerged_NotReplaced()
    {
        // Project has agent.model and agent.timeout. Issue adds agent.livenessQuietThresholdMs.
        // Final should have all three (deep merge), not just issue's.
        var projectBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { type = "opencode", model = "minimax-coding-plan/MiniMax-M3", timeout = 1800 }
            }
        }));
        var issueBundle = BundleFrom(JsonSerializer.Serialize(new
        {
            vars = new
            {
                agent = new { livenessQuietThresholdMs = 120000 }
            }
        }));

        var result = IssueVariableBuilder.Build(
            projectBundle, issueBundle, "wr_x", TestIssue(), Project);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
        Assert.Equal(1800, agent.GetProperty("timeout").GetInt32());
        Assert.Equal(120000, agent.GetProperty("livenessQuietThresholdMs").GetInt32());
    }
}
