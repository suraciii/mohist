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
        var state = Mohist.Server.Issue.Domain.MohistDefaultWorkflowProjection.Project(
            154,
            "支持中文标题 🚀",
            "todo",
            null,
            null,
            null);

        Assert.Equal("openspec/changes/issue-154", state.ChangeDir);
    }
}
