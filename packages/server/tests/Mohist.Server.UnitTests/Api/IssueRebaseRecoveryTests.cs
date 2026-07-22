using System.Text.Json;
using Mohist.Server.Api;
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
    public void ManualRebaseRecovery_ReferencesNamedPromptAndAgent_NeverInlines()
    {
        var recovery = IssueRoutes.BuildRebaseRecovery();

        var task = Assert.Single(recovery.Handlers);
        var resolve = Assert.Single(task.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", resolve.Id);
        Assert.Equal("mohist/opencode", resolve.Uses);

        // The manual rebase recovery must reuse the builtin prompt by named
        // reference (resolved by the runner at dispatch), not handroll an
        // inline prompt that drifts from the workflow-profile version.
        Assert.NotNull(resolve.With);
        Assert.Equal("${{ prompts.resolve-rebase-conflicts }}", resolve.With!["prompt"]!.Value.GetString());
        Assert.Equal("${{ vars.agent }}", resolve.With!["options"]!.Value.GetString());
        Assert.Equal("check", resolve.With!["session"]!.Value.GetString());
    }
}
