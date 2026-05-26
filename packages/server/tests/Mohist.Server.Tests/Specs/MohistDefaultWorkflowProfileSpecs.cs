using System.Text.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.WorkflowProfiles;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class MohistDefaultWorkflowProfileSpecs
{
    [Fact]
    public void IssueWithNonAsciiTitle_BuildsIssueNumberBasedOpenSpecChangeVariables()
    {
        var profile = new MohistDefaultIssueWorkflowProfile();
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
        Assert.Contains("proposal.md", proposal.With);

        var build = definition.Stages[1];
        Assert.Equal("mohist/openspec-tasks", build.TasksFromUses);
        Assert.Contains("tasks.json", build.TasksFromWith);

        var merge = definition.Stages[3].Tasks.Single(t => t.Id == "integrate:merge");
        Assert.Equal("mohist/merge", merge.Uses);
        Assert.Contains("mo/issue-${{ issue.number }}", merge.With);
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
        Assert.Equal(1, check.RetryLimit);
        Assert.Equal("fix-health", check.RetryTask?.Id);
        Assert.Contains("\"timeout\":300000", check.With);
        Assert.Contains("\"prompt\":\"Fix it\"", check.RetryTask?.With);
    }
}
