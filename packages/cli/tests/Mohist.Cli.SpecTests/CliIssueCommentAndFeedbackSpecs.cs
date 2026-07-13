using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.TestSupport;
using Xunit;

namespace Mohist.Cli.SpecTests;

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
                        body = "Looks good",
                    },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "add", "42", "--body", "Looks good"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/comments", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("Looks good", body["body"]?.GetValue<string>());
        Assert.Contains("comment_42", output.ToString());
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
                    data = new { id = "comment_99", body = "long comment..." },
                }, HttpStatusCode.Created);
            }
            return null!;
        });
        fs.AddFile("/tmp/comment.md", "long comment...");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "add", "42", "--body-file", "/tmp/comment.md"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("long comment...", body["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCommentAdd_MissingBody_PrintsValidationErrorAndExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "comment", "add", "42"], output, error, fs, executor);

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
                    data = new { id = "comment_1", body = "ok" },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "add", "42", "--body", "ok", "--project-id", "proj_xyz"],
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
                    data = new { id = "comment_json", body = "ok" },
                }, HttpStatusCode.Created);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "comment", "add", "42", "--body", "ok", "-o", "json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString())!;
        Assert.Equal("comment_json", json["id"]?.GetValue<string>());
        Assert.Equal("ok", json["body"]?.GetValue<string>());
        Assert.DoesNotContain("Created comment", output.ToString());
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
            ["issue", "feedback", "create", "42", "--stage", "plan", "--body", "ok", "--project-id", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/feedback", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueFeedbackCreate_JsonOutput_PrintsJsonEnvelope()
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
            ["issue", "feedback", "create", "42", "--stage", "plan", "--body", "ok", "-o", "json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var json = JsonNode.Parse(output.ToString())!;
        Assert.Equal("feedback_json", json["id"]?.GetValue<string>());
        Assert.Equal("plan", json["stage"]?.GetValue<string>());
        Assert.DoesNotContain("Created feedback", output.ToString());
    }
}
