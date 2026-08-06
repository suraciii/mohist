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
        repositoryName = "hello-world",
        feedMode = "start",
        approvers = new[] { "alice" },
        status = "active",
        webhookSecret = "top-secret-hex",
    };

    [Fact]
    public async Task ConnectHelp_ContainsConnectCommand()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["github", "connect", "--help"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("owner/repo", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--feed-mode", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Connect_PostsConnectionAndPrintsWebhookChecklist()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Connection(),
        }, HttpStatusCode.Created)));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["github", "connect", "octocat/hello-world", "--feed-mode", "backlog", "--approver", "alice", "--project", "proj_test"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/github-connections", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("octocat", body["owner"]!.GetValue<string>());
        Assert.Equal("hello-world", body["repo"]!.GetValue<string>());
        Assert.Equal("backlog", body["feedMode"]!.GetValue<string>());
        Assert.Equal("alice", body["approvers"]![0]!.GetValue<string>());
        var text = output.ToString();
        Assert.Contains("Payload URL:", text, StringComparison.Ordinal);
        Assert.Contains("/api/github-connections/ghconn_1/ingress", text, StringComparison.Ordinal);
        Assert.Contains("Secret:       top-secret-hex", text, StringComparison.Ordinal);
        Assert.Contains("issues, pull_request_review, check_suite", text, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret-hex", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_JsonOutput_ProjectsSelectedFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Connection(),
        }, HttpStatusCode.Created)));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["github", "connect", "octocat/hello-world", "--project", "proj_test", "--json", "id,ingressUrl"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        var json = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("ghconn_1", json["id"]!.GetValue<string>());
        Assert.Equal("http://localhost:3456/api/github-connections/ghconn_1/ingress", json["ingressUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connect_MalformedCoordinates_FailsWithoutRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["github", "connect", "not-a-coordinate", "--project", "proj_test"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("Invalid 'owner/repo'", error.ToString(), StringComparison.Ordinal);
    }
}
