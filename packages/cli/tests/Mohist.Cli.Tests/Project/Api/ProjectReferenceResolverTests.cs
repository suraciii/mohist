using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Project.Api;

public sealed class ProjectReferenceResolverTests
{
    private const string Home = "/mohist-tests/user";

    [Fact]
    public async Task ExplicitReferenceWinsOverDirectoryAndHomeState()
    {
        var files = CreateFiles("proj_home");
        files.CurrentDirectory = "/workspace/src";
        files.AddFile("/workspace/.mohist/cli-state.json", "{\"activeProjectId\":\"proj_directory\"}");

        var result = await Resolver(files).ResolveAsync("proj_explicit");

        var resolved = Assert.IsType<ProjectReferenceResolver.Result.Resolved>(result);
        Assert.Equal("proj_explicit", resolved.ProjectReference);
    }

    [Fact]
    public async Task NearestDirectoryContextWinsOverHomeState()
    {
        var files = CreateFiles("proj_home");
        files.CurrentDirectory = "/workspace/src/nested";
        files.AddFile("/workspace/.mohist/cli-state.json", "{\"activeProjectId\":\"proj_directory\"}");

        var result = await Resolver(files).ResolveAsync(null);

        var resolved = Assert.IsType<ProjectReferenceResolver.Result.Resolved>(result);
        Assert.Equal("proj_directory", resolved.ProjectReference);
    }

    [Fact]
    public async Task HomeStateIsUsedWhenDirectoryHasNoContext()
    {
        var files = CreateFiles("proj_home");

        var result = await Resolver(files).ResolveAsync(null);

        var resolved = Assert.IsType<ProjectReferenceResolver.Result.Resolved>(result);
        Assert.Equal("proj_home", resolved.ProjectReference);
    }

    [Fact]
    public async Task MalformedNearestContextDoesNotFallThroughToHomeState()
    {
        var files = CreateFiles("proj_home");
        files.CurrentDirectory = "/workspace/src";
        files.AddFile("/workspace/.mohist/cli-state.json", "{\"activeProjectId\":\"\"}");

        var result = await Resolver(files).ResolveAsync(null);

        var invalid = Assert.IsType<ProjectReferenceResolver.Result.Invalid>(result);
        Assert.Contains("current-directory context", invalid.Source);
    }

    [Fact]
    public async Task MissingContextIsReportedWithoutARequest()
    {
        var files = new FakeFileSystem();

        var result = await Resolver(files).ResolveAsync(null);

        Assert.IsType<ProjectReferenceResolver.Result.Missing>(result);
    }

    private static ProjectReferenceResolver Resolver(FakeFileSystem files) =>
        new(files, () => Home);

    private static FakeFileSystem CreateFiles(string homeProjectId)
    {
        var files = new FakeFileSystem();
        files.AddFile(
            ProjectReferenceResolver.StatePath(Home),
            $"{{\"activeProjectId\":\"{homeProjectId}\"}}");
        return files;
    }
}
