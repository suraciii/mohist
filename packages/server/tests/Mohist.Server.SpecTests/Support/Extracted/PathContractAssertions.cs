using System.Text.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Support;

internal static class PathContractAssertions
{
    public static void AssertProjectHasNoLocalPathFields(JsonElement project)
    {
        Assert.Equal(JsonValueKind.Object, project.ValueKind);
        Assert.False(project.TryGetProperty("path", out _), "project response unexpectedly contained 'path'");
        Assert.False(project.TryGetProperty("effectivePath", out _), "project response unexpectedly contained 'effectivePath'");
        Assert.False(project.TryGetProperty("checkoutPath", out _), "project response unexpectedly contained 'checkoutPath'");
        Assert.False(project.TryGetProperty("baseBranch", out _), "project response unexpectedly contained 'baseBranch'");

        if (project.TryGetProperty("repositories", out var repositories)
            && repositories.ValueKind == JsonValueKind.Array)
        {
            foreach (var repo in repositories.EnumerateArray())
                AssertRepositoryHasNoLocalPathFields(repo);
        }
    }

    public static void AssertRepositoryHasNoLocalPathFields(JsonElement repo)
    {
        Assert.Equal(JsonValueKind.Object, repo.ValueKind);
        Assert.False(repo.TryGetProperty("path", out _), "repository response unexpectedly contained 'path'");
        Assert.False(repo.TryGetProperty("remote", out _), "repository response unexpectedly contained 'remote'");
        Assert.False(repo.TryGetProperty("resolvedPath", out _), "repository response unexpectedly contained 'resolvedPath'");
        Assert.True(repo.TryGetProperty("gitUrl", out _), "repository response missing 'gitUrl'");
        Assert.True(repo.TryGetProperty("baseBranch", out _), "repository response missing 'baseBranch'");
    }

    public static void AssertDispatchVariablesHaveWorkspaceContract(JsonElement variables)
    {
        if (variables.ValueKind != JsonValueKind.Object)
        {
            Assert.Fail($"expected dispatch variables to be a JSON object, got {variables.ValueKind}");
            return;
        }

        if (variables.TryGetProperty("project", out var projectVar)
            && projectVar.ValueKind == JsonValueKind.Object)
        {
            Assert.False(projectVar.TryGetProperty("path", out _), "project dispatch variable unexpectedly contained 'path'");
            Assert.False(projectVar.TryGetProperty("effectivePath", out _), "project dispatch variable unexpectedly contained 'effectivePath'");
            Assert.False(projectVar.TryGetProperty("baseBranch", out _), "project dispatch variable unexpectedly contained 'baseBranch'");
            Assert.False(projectVar.TryGetProperty("defaultBranch", out _), "project dispatch variable unexpectedly contained 'defaultBranch'");
        }

        if (variables.TryGetProperty("repository", out var repoVar)
            && repoVar.ValueKind == JsonValueKind.Object)
        {
            Assert.False(repoVar.TryGetProperty("path", out _), "repository dispatch variable unexpectedly contained 'path'");
            Assert.False(repoVar.TryGetProperty("remote", out _), "repository dispatch variable unexpectedly contained 'remote'");
            Assert.False(repoVar.TryGetProperty("resolvedPath", out _), "repository dispatch variable unexpectedly contained 'resolvedPath'");
            Assert.True(repoVar.TryGetProperty("gitUrl", out _), "repository dispatch variable missing 'gitUrl'");
            Assert.True(repoVar.TryGetProperty("baseBranch", out _), "repository dispatch variable missing 'baseBranch'");
        }

        Assert.True(variables.TryGetProperty("workspace", out var workspace),
            "dispatch variables missing 'workspace'");
        if (workspace.ValueKind == JsonValueKind.Object)
        {
            Assert.True(workspace.TryGetProperty("path", out _), "workspace dispatch variable missing 'path'");
            Assert.True(workspace.TryGetProperty("branch", out _), "workspace dispatch variable missing 'branch'");
        }
    }
}
