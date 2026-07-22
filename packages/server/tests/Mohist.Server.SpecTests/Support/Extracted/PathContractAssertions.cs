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

    public static void AssertEffectiveVariablesContainNoRuntimeContext(JsonElement variables)
    {
        if (variables.ValueKind != JsonValueKind.Object)
        {
            Assert.Fail($"expected effective variables to be a JSON object, got {variables.ValueKind}");
            return;
        }

        Assert.False(variables.TryGetProperty("mohist", out _));
        Assert.False(variables.TryGetProperty("project", out _));
        Assert.False(variables.TryGetProperty("repository", out _));
        Assert.False(variables.TryGetProperty("workspace", out _));
    }
}
