using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliRepositoryCommandSpecs
{
    [Fact]
    public async Task RepositoryAdd_WithGitUrl_SendsGitUrlMetadataAndNoPathFields()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestHarness.Create(
            async (_, _) => RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "proj_123",
                    repositories = new[]
                    {
                        new { name = "origin", gitUrl = "git@example.com:repo.git", baseBranch = "main", isDefault = true },
                    },
                },
            }, HttpStatusCode.Created),
            "proj_123");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repository", "add", "origin", "--git-url", "git@example.com:repo.git", "--base-branch", "main", "--default"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_123/repositories", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!);
        Assert.Equal("origin", body!["name"]?.GetValue<string>());
        Assert.Equal("git@example.com:repo.git", body["gitUrl"]?.GetValue<string>());
        Assert.Equal("main", body["baseBranch"]?.GetValue<string>());
        Assert.True(body["isDefault"]?.GetValue<bool>());
        Assert.False(body.AsObject().ContainsKey("path"));
        Assert.False(body.AsObject().ContainsKey("remote"));
        Assert.False(body.AsObject().ContainsKey("resolvedPath"));
    }

    [Fact]
    public async Task RepositoryUpdate_WithGitUrl_SendsGitUrlMetadataAndNoPathFields()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestHarness.Create(
            async (_, _) => RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "proj_123",
                    repositories = new[]
                    {
                        new { name = "upstream", gitUrl = "git@example.com:repo-v2.git", baseBranch = "develop", isDefault = false },
                    },
                },
            }),
            "proj_123");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repository", "update", "origin", "--new-name", "upstream", "--git-url", "git@example.com:repo-v2.git", "--base-branch", "develop"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/projects/proj_123/repositories/origin", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!);
        Assert.Equal("upstream", body!["newName"]?.GetValue<string>());
        Assert.Equal("git@example.com:repo-v2.git", body["gitUrl"]?.GetValue<string>());
        Assert.Equal("develop", body["baseBranch"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("path"));
        Assert.False(body.AsObject().ContainsKey("remote"));
        Assert.False(body.AsObject().ContainsKey("resolvedPath"));
    }

    [Fact]
    public async Task RepositoryAdd_WithoutGitUrl_IsRejectedWithClearError()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestHarness.Create(
            async (_, _) => RecordingHttpHandler.JsonError("gitUrl is required", "repository_giturl_required"),
            "proj_123");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["repository", "add", "origin"],
            output,
            error,
            fileSystem,
            executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("git-url", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }
}
