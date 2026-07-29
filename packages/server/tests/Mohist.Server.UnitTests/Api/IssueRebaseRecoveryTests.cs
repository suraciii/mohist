using System.Text.Json;
using Mohist.Server.Api;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public class IssueRebaseRecoveryTests
{
    [Fact]
    public void BuildRebaseTaskWith_CarriesOnlyDeclaredRebaseInputs()
    {
        var input = IssueRoutes.BuildRebaseTaskWith("release");

        Assert.NotNull(input);
        var with = input!.Value;
        Assert.Equal("release", with.GetProperty("baseBranch").GetString());
        Assert.Equal("origin", with.GetProperty("remote").GetString());

        var declaredKeys = new HashSet<string>(
            with.EnumerateObject().Select(p => p.Name),
            StringComparer.Ordinal);
        Assert.Equal(new[] { "baseBranch", "remote" }, declaredKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.False(
            with.TryGetProperty("repository", out _),
            "The rebase task's with must not carry a 'repository' key — the mohist/rebase Action does not declare it as an input.");
    }

    [Fact]
    public void ManualRebaseRecovery_AuthorsRecoveryDefinitionInWorkflowContent()
    {
        var recovery = WorkflowProfileCatalog.Definition.Recoveries;
        Assert.NotNull(recovery);
        Assert.True(recovery!.TryGetValue("rebase-conflicts", out var template));
        Assert.NotNull(template);

        Assert.Equal(2, template!.Budget);
        var handler = Assert.Single(template.Handlers);
        Assert.Equal("error.code=conflict", handler.When);
        Assert.False(handler.RetrySelf);
        var task = Assert.Single(handler.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", task.Id);
        Assert.Equal("Resolve rebase conflicts", task.Title);
        Assert.Equal("mohist/opencode", task.Uses);

        // The rebase recovery must reuse the builtin prompt by named
        // reference (resolved by the runner at dispatch), not handroll an
        // inline prompt that drifts from the workflow-profile version.
        Assert.NotNull(task.With);
        Assert.Equal("${{ prompts.resolve-rebase-conflicts }}", task.With!["prompt"]!.Value.GetString());
        Assert.Equal("${{ vars.agent }}", task.With!["options"]!.Value.GetString());
        Assert.Equal("check", task.With!["session"]!.Value.GetString());
    }

    [Fact]
    public void GithubPrWorkflowDefinition_DeclaresRebaseConflictsRecoveryTemplate()
    {
        var recovery = WorkflowProfileCatalog.GithubPrWorkflowDefinition.Recoveries;
        Assert.NotNull(recovery);
        Assert.True(recovery!.TryGetValue("rebase-conflicts", out var template));
        Assert.NotNull(template);

        Assert.Equal(2, template!.Budget);
        var handler = Assert.Single(template.Handlers);
        Assert.Equal("error.code=conflict", handler.When);
        Assert.False(handler.RetrySelf);
        var task = Assert.Single(handler.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", task.Id);
        Assert.Equal("mohist/opencode", task.Uses);
        Assert.Equal("${{ prompts.resolve-rebase-conflicts }}", task.With!["prompt"]!.Value.GetString());
    }

    [Fact]
    public void IssueRoutesHelpers_DoesNotExposeBuildRebaseRecovery()
    {
        var helperType = typeof(IssueRoutes);
        var method = helperType.GetMethod(
            "BuildRebaseRecovery",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.Null(method);
    }
}
