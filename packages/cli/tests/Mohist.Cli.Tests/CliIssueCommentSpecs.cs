using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueCommentSpecs
{
    [Fact]
    public async Task IssueCommentAdd_SendsPostWithBodyAndPrintsNewCommentId()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.Method == HttpMethod.Post
                ? RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "comment_42", author = "Ada Lovelace", body = "Looks good" },
                }, HttpStatusCode.Created)
                : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "create", "42", "--display-name", "  Ada Lovelace  ", "--body", "Looks good"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/comments", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("Looks good", body["body"]?.GetValue<string>());
        Assert.Equal("Ada Lovelace", body["displayName"]?.GetValue<string>());
        Assert.Contains("comment_42", output.ToString());
    }

    [Fact]
    public async Task IssueCommentAdd_JsonOutputProjectsCanonicalCommentFields()
    {
        const string createdAt = "2026-08-10T12:34:56.0000000+00:00";
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.Method == HttpMethod.Post
                ? RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "comment_42",
                        projectId = "proj_abc",
                        issueNumber = 42,
                        body = "Looks good",
                        author = "service",
                        displayName = "Ada Lovelace",
                        createdAt,
                    },
                }, HttpStatusCode.Created)
                : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "create", "42", "--display-name", "Ada Lovelace", "--body", "Looks good", "--json", "id,projectId,issueNumber,body,createdAt,author,displayName"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("comment_42", result["id"]?.GetValue<string>());
        Assert.Equal("proj_abc", result["projectId"]?.GetValue<string>());
        Assert.Equal(42, result["issueNumber"]?.GetValue<int>());
        Assert.Equal("Looks good", result["body"]?.GetValue<string>());
        Assert.Equal("service", result["author"]?.GetValue<string>());
        Assert.Equal("Ada Lovelace", result["displayName"]?.GetValue<string>());
        Assert.Equal(createdAt, result["createdAt"]?.GetValue<string>());
        Assert.Empty(error.ToString());
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task IssueCommentAdd_FromFile_ReadsFileAndSendsContentsAsBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.Method == HttpMethod.Post
                ? RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "comment_99", author = "Grace Hopper", body = "long comment..." },
                }, HttpStatusCode.Created)
                : null!);
        fs.AddFile("/tmp/comment.md", "long comment...");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "create", "42", "--display-name", "Grace Hopper", "--body-file", "/tmp/comment.md"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Last(r => r.Method == HttpMethod.Post).Body!)!;
        Assert.Equal("long comment...", body["body"]?.GetValue<string>());
        Assert.Equal("Grace Hopper", body["displayName"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCommentAdd_MissingBody_FailsWithScopedUsageBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "create", "42", "--display-name", "Ada"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("comment body is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("USAGE", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo issue comment create <number> [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
