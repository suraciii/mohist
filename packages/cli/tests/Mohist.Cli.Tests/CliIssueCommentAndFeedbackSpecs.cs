using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueCommentAndFeedbackSpecs
{
    [Fact]
    public async Task IssueCommentAdd_SuccessPath_SendsPostWithBodyAndPrintsNewCommentId()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "comment_42",
                        author = "Ada Lovelace",
                        body = "Looks good",
                    },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "add", "42", "--author", "  Ada Lovelace  ", "--body", "Looks good"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/comments", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("Looks good", body["body"]?.GetValue<string>());
        Assert.Equal("Ada Lovelace", body["author"]?.GetValue<string>());
        Assert.Contains("comment_42", output.ToString());
        Assert.Contains("Ada Lovelace", output.ToString());
    }

    [Fact]
    public async Task IssueCommentAdd_FromFile_ReadsFileAndSendsContentsAsBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "comment_99", author = "Grace Hopper", body = "long comment..." },
                }, HttpStatusCode.Created);
            }
            return null!;
        });
        fs.AddFile("/tmp/comment.md", "long comment...");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "add", "42", "--author", "Grace Hopper", "--body-file", "/tmp/comment.md"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("long comment...", body["body"]?.GetValue<string>());
        Assert.Equal("Grace Hopper", body["author"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCommentAdd_MissingBody_PrintsValidationErrorAndExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "add", "42", "--author", "Ada"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var stderr = error.ToString();
        Assert.Contains("--body", stderr);
    }

    [Fact]
    public async Task IssueComment_HelpListsAddSubcommand()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("add", stdout);
    }

    [Fact]
    public async Task IssueCommentAdd_AcceptsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "comment_1", author = "Ada", body = "ok" },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "add", "42", "--author", "Ada", "--body", "ok", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/comments", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueCommentAdd_JsonOutput_PrintsJsonEnvelope()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "comment_json", author = "Ada", body = "ok" },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "add", "42", "--author", "Ada", "--body", "ok", "--json", "id"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString())!;
        Assert.Equal("comment_json", json["id"]?.GetValue<string>());
        Assert.Equal("ok", json["body"]?.GetValue<string>());
        Assert.Equal("Ada", json["author"]?.GetValue<string>());
        Assert.DoesNotContain("Created comment", output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IssueCommentAdd_MissingOrBlankAuthor_FailsBeforePost(string? author)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();
        var args = new List<string> { "issue", "comment", "add", "42", "--body", "Looks good" };
        if (author is not null)
        {
            args.Add("--author");
            args.Add(author);
        }

        var exitCode = await MohistCliCommands.RunAsync(http, args.ToArray(), output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("--author", error.ToString());
        Assert.Contains("required", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueCommentAdd_AuthorOverLimit_FailsBeforePost()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "add", "42", "--author", new string('x', 101), "--body", "Looks good"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("100", error.ToString());
    }

    [Fact]
    public async Task IssueCommentAdd_TableOutput_ShowsRecordedAuthorAndBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(request =>
            request.Method == HttpMethod.Post
                ? RecordingHttpHandler.Json(new { success = true, data = new { id = "comment_table", author = "Ada", body = "Looks good" } })
                : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "add", "42", "--author", "Ada", "--body", "Looks good",],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("author:  Ada", output.ToString());
        Assert.Contains("body:    Looks good", output.ToString());
    }

    [Fact]
    public async Task IssueCommentAdd_ServerValidation_SurfacesActionableMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(request =>
            request.Method == HttpMethod.Post
                ? RecordingHttpHandler.JsonError("Comment author must be 100 characters or fewer.", "validation", HttpStatusCode.BadRequest)
                : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "add", "42", "--author", "Ada", "--body", "Looks good"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("100 characters", output.ToString() + error.ToString());
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task IssueFeedbackCreate_SuccessPath_SendsPostWithStageAndBodyAndPrintsNewId()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "feedback_7",
                        stage = "plan",
                        body = "Rethink the data model",
                    },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "feedback", "create", "42", "--stage", "plan", "--body", "Rethink the data model"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/feedback", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("plan", body["stage"]?.GetValue<string>());
        Assert.Equal("Rethink the data model", body["body"]?.GetValue<string>());
        Assert.Contains("feedback_7", output.ToString());
    }

    [Fact]
    public async Task IssueFeedbackCreate_FromFile_ReadsFileAndSendsContentsAsBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "feedback_8", stage = "build", body = "..." },
                }, HttpStatusCode.Created);
            }
            return null!;
        });
        fs.AddFile("/tmp/feedback.md", "long feedback body");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "feedback", "create", "42", "--stage", "build", "--body-file", "/tmp/feedback.md"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("build", body["stage"]?.GetValue<string>());
        Assert.Equal("long feedback body", body["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueFeedbackCreate_MissingStage_PrintsValidationErrorAndExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "feedback", "create", "42", "--body", "Some feedback"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var stderr = error.ToString();
        Assert.Contains("--stage", stderr);
        Assert.Contains("required", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueFeedbackCreate_MissingBody_PrintsValidationErrorAndExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "feedback", "create", "42", "--stage", "plan"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var stderr = error.ToString();
        Assert.Contains("--body", stderr);
    }

    [Fact]
    public async Task IssueFeedback_HelpListsCreateAlongsideListAndShow()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "feedback", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("create", stdout);
        Assert.Contains("list", stdout);
        Assert.Contains("show", stdout);
    }

    [Fact]
    public async Task IssueFeedbackCreate_AcceptsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "feedback_1", stage = "plan", body = "ok" },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "feedback", "create", "42", "--stage", "plan", "--body", "ok", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/feedback", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueFeedbackCreate_SelectedJson_ProjectsRequestedFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "feedback_json", stage = "plan", body = "ok" },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "feedback", "create", "42", "--stage", "plan", "--body", "ok", "--json", "id,stage,body"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString())!;
        Assert.Equal("feedback_json", json["id"]?.GetValue<string>());
        Assert.Equal("plan", json["stage"]?.GetValue<string>());
        Assert.DoesNotContain("Created feedback", output.ToString());
    }
}
