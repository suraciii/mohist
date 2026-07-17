using Mohist.Server.Api;
using Mohist.Server.Project.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public class IssueRebaseRecoveryTests
{
    [Fact]
    public void BuildRebaseTaskWith_UsesResolvedRepositoryContext()
    {
        var repository = new RepositoryInfo
        {
            Name = "secondary",
            GitUrl = "git@secondary.example:repo.git",
            BaseBranch = "release",
        };

        var input = IssueRoutes.BuildRebaseTaskWith("release", repository);

        Assert.NotNull(input);
        Assert.Equal("release", input!.Value.GetProperty("baseBranch").GetString());
        Assert.Equal("origin", input.Value.GetProperty("remote").GetString());
        var taskRepository = input.Value.GetProperty("repository");
        Assert.Equal("secondary", taskRepository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:repo.git", taskRepository.GetProperty("gitUrl").GetString());
        Assert.Equal("release", taskRepository.GetProperty("baseBranch").GetString());
    }

    [Fact]
    public void ManualRebaseRecovery_ReferencesNamedPromptAndAgent_NeverInlines()
    {
        var recovery = IssueRoutes.BuildRebaseRecovery();

        var task = Assert.Single(recovery.Handlers);
        var resolve = Assert.Single(task.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", resolve.Id);
        Assert.Equal("mohist/acp-agent", resolve.Uses);

        // The manual rebase recovery must reuse the builtin prompt by named
        // reference (resolved by the runner at dispatch), not handroll an
        // inline prompt that drifts from the workflow-profile version.
        Assert.NotNull(resolve.With);
        Assert.Equal("${{ prompts.resolve-rebase-conflicts }}", resolve.With!["prompt"]!.Value.GetString());
        Assert.Equal("${{ vars.agent }}", resolve.With!["options"]!.Value.GetString());
        Assert.Equal("check", resolve.With!["session"]!.Value.GetString());
    }
}
