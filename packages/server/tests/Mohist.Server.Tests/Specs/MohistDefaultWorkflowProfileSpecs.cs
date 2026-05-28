using System.Text.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Workflow.Prompts;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class FakePromptLoader : IPromptLoader
{
    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal)
    {
        ["proposal"] = "# Proposal Artifact\nCreate proposal.md",
        ["specs"] = "# Specs Artifact\nCreate specs",
        ["design"] = "# Design Artifact\nCreate design.md",
        ["tasks"] = "# Tasks Artifact\nCreate tasks.json",
        ["self-review"] = "# Self Review\nReview artifacts",
        ["review"] = "# Review\nReview implementation",
        ["build"] = "# Build\nImplement task",
    };

    public string Load(string name) => Prompts.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"Prompt '{name}' not found");
    public Dictionary<string, string> LoadAll() => new(Prompts, StringComparer.Ordinal);
}

public class MohistDefaultWorkflowProfileSpecs
{
    [Fact]
    public void IssueWithNonAsciiTitle_BuildsIssueNumberBasedOpenSpecChangeVariables()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader());
        var issue = new Mohist.Server.Issue.Domain.Issue("issue-154", "project-1", 154, "支持中文标题 🚀");

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", "/repo", "main"));

        using var document = JsonDocument.Parse(variables);
        Assert.Equal("issue-154", document.RootElement.GetProperty("openspecChangeName").GetString());
        Assert.Equal("openspec/changes/issue-154", document.RootElement.GetProperty("openspecChangeDir").GetString());
        Assert.False(document.RootElement.TryGetProperty("artifacts", out _));
    }

    [Fact]
    public void IssueWithNonAsciiTitle_ProjectsIssueNumberBasedChangeDir()
    {
        var state = MohistDefaultWorkflowProjection.ProjectWorkflowState(
            154,
            "支持中文标题 🚀",
            "todo",
            null,
            null,
            null);

        Assert.Equal("openspec/changes/issue-154", state.ChangeDir);
    }

    [Fact]
    public void DefaultWorkflowDefinition_LoadsFromYaml()
    {
        var definition = MohistWorkflow.Definition;

        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());
        Assert.True(definition.Stages[0].RequiresApproval);
        Assert.True(definition.Stages[2].RequiresApproval);

        var proposal = definition.Stages[0].Tasks[0];
        Assert.Equal("proposal", proposal.Id);
        Assert.Equal("mohist/acp-agent", proposal.Uses);
        Assert.Contains("proposal.md", JsonSerializer.Serialize(proposal.With));

        var build = definition.Stages[1];
        Assert.Equal("mohist/openspec-tasks", build.TasksFrom?.Uses);
        Assert.Contains("tasks.json", JsonSerializer.Serialize(build.TasksFrom?.With));

        var merge = definition.Stages[3].Tasks.Single(t => t.Id == "integrate:merge");
        Assert.Equal("mohist/merge", merge.Uses);
        Assert.Contains("mo/issue-${{ issue.number }}", JsonSerializer.Serialize(merge.With));
    }

    [Fact]
    public void AgentConfig_MergesGlobalConfigIntoAgentVariable()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader());
        var issue = new Mohist.Server.Issue.Domain.Issue(
            "issue-1",
            "project-1",
            1,
            "Agent config");

        var variables = profile.BuildVariables(
            "wr-1",
            issue,
            new WorkflowProjectContext("project-1", "Mohist", "/repo", "main"),
            new Dictionary<string, object?> { ["model"] = "openai/gpt-4o", ["probeTimeoutMs"] = 30000 });

        using var document = JsonDocument.Parse(variables);
        var agent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal(30000, agent.GetProperty("probeTimeoutMs").GetInt32());
    }

    [Fact]
    public void StageVariables_MergesStageOverrides()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader());
        var issue = new Mohist.Server.Issue.Domain.Issue("issue-1", "project-1", 1, "Stage vars");

        var stageVariables = profile.BuildStageVariables(
            issue,
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["check"] = new() { ["model"] = "openai/o3" },
            });

        Assert.NotNull(stageVariables);
        Assert.True(stageVariables.ContainsKey("check"));
    }

    [Fact]
    public void BuildVariables_IncludesPromptsFromLoader()
    {
        var loader = new FakePromptLoader();
        var profile = new MohistDefaultIssueWorkflowProfile(loader);
        var issue = new Mohist.Server.Issue.Domain.Issue("issue-1", "project-1", 1, "Test");

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", "/repo", "main"));

        using var document = JsonDocument.Parse(variables);
        var prompts = document.RootElement.GetProperty("prompts");
        Assert.Equal("# Proposal Artifact\nCreate proposal.md", prompts.GetProperty("proposal").GetString());
        Assert.Equal("# Build\nImplement task", prompts.GetProperty("build").GetString());
        Assert.Equal(7, prompts.EnumerateObject().Count());
    }

    [Fact]
    public void WorkflowYamlParser_ParsesRetryTasksAndWithObjects()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks:
              - name: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
                  timeout: 300000
                retryLimit: 1
                retryTask:
                  id: fix-health
                  title: Fix health
                  uses: mohist/acp-agent
                  with:
                    prompt: Fix it
        """);

        var check = definition.Stages.Single().Checks.Single();
        Assert.Equal("core/script", check.Uses);
        Assert.Equal(1, check.OnFailure?.Retry?.Limit);
        Assert.Equal("fix-health", check.OnFailure?.Retry?.Task.Id);
        Assert.Contains("\"timeout\":300000", JsonSerializer.Serialize(check.With));
        Assert.Contains("\"prompt\":\"Fix it\"", JsonSerializer.Serialize(check.OnFailure?.Retry?.Task.With));
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsDomainDefinition()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(MohistWorkflow.Definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.Contains("agent: ${{ vars.agent }}", yaml);
        Assert.Contains("prompt: ${{ prompts.proposal }}", yaml);
        Assert.Equal("mohist/openspec-tasks", reparsed.Stages[1].TasksFrom?.Uses);
        Assert.Equal(2, reparsed.Stages[2].Checks.Single(c => c.Name == "review-passed").OnFailure?.Retry?.Limit);
    }
}
