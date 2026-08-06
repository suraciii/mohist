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
    public async Task UpdateHelp_ContainsUpdateCommand()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["github", "update", "--help"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("connection-id", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--approver", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-approvers", output.ToString(), StringComparison.Ordinal);
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

    [Fact]
    public async Task UpdateApprovers_PatchesConnectionAndPrintsResult()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                id = "ghconn_1",
                projectId = "proj_abc",
                owner = "octocat",
                repo = "hello-world",
                repositoryName = "hello-world",
                feedMode = "start",
                approvers = new[] { "alice", "bob" },
                status = "active",
                webhookSecret = (string?)null,
            },
        })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["github", "update", "ghconn_1", "--approver", "alice", "--approver", "bob", "--project", "proj_test"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/projects/proj_test/github-connections/ghconn_1", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal(new[] { "alice", "bob" }, body["approvers"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray());
        Assert.Contains("approvers = alice, bob", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task UpdateApprovers_NoApprovers_ClearsList()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                id = "ghconn_1",
                projectId = "proj_abc",
                owner = "octocat",
                repo = "hello-world",
                repositoryName = "hello-world",
                feedMode = "start",
                approvers = Array.Empty<string>(),
                status = "active",
                webhookSecret = (string?)null,
            },
        })));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["github", "update", "ghconn_1", "--no-approvers", "--project", "proj_test"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.NotNull(body["approvers"]);
        Assert.Empty(body["approvers"]!.AsArray());
        Assert.Contains("approvers = (none)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateApprovers_WithoutOptions_FailsWithoutRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["github", "update", "ghconn_1", "--project", "proj_test"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("Specify --approver or --no-approvers", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateApprovers_ApproverWithNoApprovers_FailsWithoutRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["github", "update", "ghconn_1", "--approver", "alice", "--no-approvers", "--project", "proj_test"],
            output,
            error,
            fs,
            executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
    }
}
