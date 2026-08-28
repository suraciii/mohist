using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliGithubCommandSpecs
{
    private static object Connection() => new
    {
        id = "ghconn_1",
        projectId = "proj_abc",
        owner = "octocat",
        repo = "hello-world",
        repositoryName = "main",
        approvers = new[] { "alice" },
        status = "active",
        installationId = "installation-1",
        repositoryNodeId = "repository-node-1",
        reconnectRequired = false,
        needsAttention = false,
        needsReprojection = false,
        lastError = (object?)null,
        webhookSecret = "top-secret-hex",
    };

    [Fact]
    public async Task ConnectHelp_DoesNotExposePat()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exit = await MohistCliCommands.RunAsync(http, ["github", "connect", "--help"], output, error, fs, executor);
        Assert.Equal(0, exit);
        Assert.Contains("owner/repo", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--pat", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Connect_PostsOnlyRepositoryAndPrintsWebhookChecklist()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Connection() }, HttpStatusCode.Created)));
        var exit = await MohistCliCommands.RunAsync(
            http, ["github", "connect", "octocat/hello-world", "--approver", "alice", "--project", "proj_test"], output, error, fs, executor);
        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("octocat", body["owner"]!.GetValue<string>());
        Assert.False(body.ContainsKey("pat"));
        Assert.Contains("GitHub App installation verified.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Payload URL:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_PatOptionIsRejectedWithoutRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var exit = await MohistCliCommands.RunAsync(
            http, ["github", "connect", "octocat/hello-world", "--pat", "secret", "--project", "proj_test"], output, error, fs, executor);
        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task List_RequestsGitHubConnections()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new[] { Connection() } })));
        var exit = await MohistCliCommands.RunAsync(http, ["github", "list", "--project", "proj_test"], output, error, fs, executor);
        Assert.Equal(0, exit);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
        Assert.Equal("/api/projects/proj_test/github-connections", handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains("repository", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task View_RequestsOneConnection()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Connection() })));
        var exit = await MohistCliCommands.RunAsync(http, ["github", "view", "ghconn_1", "--project", "proj_test"], output, error, fs, executor);
        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_test/github-connections/ghconn_1", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EnableAndDisable_UseConnectionStatusRoutes()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Connection() })));
        Assert.Equal(0, await MohistCliCommands.RunAsync(http, ["github", "enable", "ghconn_1", "--project", "proj_test"], output, error, fs, executor));
        Assert.Equal("/api/projects/proj_test/github-connections/ghconn_1/enable", handler.Requests.Single().RequestUri?.PathAndQuery);
        handler.Requests.Clear();
        Assert.Equal(0, await MohistCliCommands.RunAsync(http, ["github", "disable", "ghconn_1", "--project", "proj_test"], output, error, fs, executor));
        Assert.Equal("/api/projects/proj_test/github-connections/ghconn_1/disable", handler.Requests.Single().RequestUri?.PathAndQuery);
    }
}
