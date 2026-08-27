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
        Assert.DoesNotContain("--feed-mode", output.ToString(), StringComparison.Ordinal);
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
            ["github", "connect", "octocat/hello-world", "--approver", "alice", "--pat", "github-pat", "--project", "proj_test"],
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
        Assert.Equal("alice", body["approvers"]![0]!.GetValue<string>());
        Assert.Equal("github-pat", body["pat"]!.GetValue<string>());
        var text = output.ToString();
        Assert.DoesNotContain("github-pat", text, StringComparison.Ordinal);
        Assert.Contains("Payload URL:", text, StringComparison.Ordinal);
        Assert.Contains("/api/github-connections/ghconn_1/ingress", text, StringComparison.Ordinal);
        Assert.Contains("Secret:       top-secret-hex", text, StringComparison.Ordinal);
        Assert.Contains("issues, issue_comment, pull_request_review, check_suite", text, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret-hex", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_RequiresPatWithoutSendingRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["github", "connect", "octocat/hello-world", "--project", "proj_test"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("--pat is required", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_RemovedFeedOption_IsRejected()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["github", "connect", "octocat/hello-world", "--feed-mode", "backlog", "--project", "proj_test"],
            output,
            error,
            fs,
            executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
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
            ["github", "connect", "octocat/hello-world", "--pat", "github-pat", "--project", "proj_test", "--json", "id,ingressUrl"],
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
    public async Task IssueGitHubSync_PostsSyncRequestAndPrintsIssue()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                number = 42,
                title = "Issue",
                github = new
                {
                    repository = "octocat/hello-world",
                    number = 817,
                    url = "https://github.com/octocat/hello-world/issues/817",
                    syncStatus = "healthy",
                },
            },
        })));

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "github", "sync", "42", "--project", "proj_test", "--json", "number,github"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/issues/42/github/sync", request.RequestUri?.PathAndQuery);
        Assert.Contains("\"number\": 42", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task IssueGitHubLink_ParsesCoordinatesAndPostsPairing()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                number = 42,
                title = "Issue",
                github = new
                {
                    repository = "octocat/hello-world",
                    number = 817,
                    url = "https://github.com/octocat/hello-world/issues/817",
                    syncStatus = "healthy",
                },
            },
        })));

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "github", "link", "42", "octocat/hello-world#817", "--project", "proj_test", "--json", "number,github"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/issues/42/github/link", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("octocat/hello-world", body["repository"]!.GetValue<string>());
        Assert.Equal(817, body["number"]!.GetValue<int>());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task IssueGitHubUnlink_PostsUnlinkRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new { number = 42, title = "Issue", github = (object?)null },
        })));

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "github", "unlink", "42", "--project", "proj_test", "--json", "number"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/issues/42/github/unlink", request.RequestUri?.PathAndQuery);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task IssueGitHubLink_MalformedCoordinatesFailsWithoutRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["issue", "github", "link", "42", "octocat/hello-world", "--project", "proj_test"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("owner/repo#number", error.ToString(), StringComparison.Ordinal);
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
