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
            http, ["issue", "comment", "create", "42", "--author", "  Ada Lovelace  ", "--body", "Looks good"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/comments", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("Looks good", body["body"]?.GetValue<string>());
        Assert.Equal("Ada Lovelace", body["author"]?.GetValue<string>());
        Assert.Contains("comment_42", output.ToString());
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
            http, ["issue", "comment", "create", "42", "--author", "Grace Hopper", "--body-file", "/tmp/comment.md"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Last(r => r.Method == HttpMethod.Post).Body!)!;
        Assert.Equal("long comment...", body["body"]?.GetValue<string>());
        Assert.Equal("Grace Hopper", body["author"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCommentAdd_MissingBody_FailsWithScopedUsageBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "create", "42", "--author", "Ada"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("comment body is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo issue comment create <number> [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
