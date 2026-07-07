using Mohist.Server.Api;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.Api)]
public class IssueRebaseRecoverySpecs
{
    [Fact]
    public void ManualRebaseRecovery_ReferencesNamedPromptAndAgent_NeverInlines()
    {
        var recovery = IssueRoutes.BuildRebaseRecovery();

        var task = Assert.Single(recovery.Handlers);
        var resolve = Assert.Single(task.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", resolve.Id);
        Assert.Equal("mohist/acp-agent", resolve.Uses);

        // The manual rebase recovery must reuse the builtin prompt by named
        // reference (resolved by the runner at dispatch), not hand-roll an
        // inline prompt that drifts from the workflow-profile version.
        Assert.NotNull(resolve.With);
        Assert.Equal("${{ prompts.resolve-rebase-conflicts }}", resolve.With!["prompt"]!.Value.GetString());
        Assert.Equal("${{ vars.agent }}", resolve.With!["agent"]!.Value.GetString());
        Assert.Equal("check", resolve.With!["session"]!.Value.GetString());
    }
}
