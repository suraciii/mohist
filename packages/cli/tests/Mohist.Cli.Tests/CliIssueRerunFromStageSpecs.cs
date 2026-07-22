using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueRerunFromStageSpecs
{
    [Fact]
    public async Task IssueRerunFromStage_SuccessPath_SendsPostWithStageBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "rerun-from-stage", "42", "--stage", "build"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("build", body["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueRerunFromStage_MissingStage_PrintsUsageErrorAndMakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "rerun-from-stage", "42"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var stderr = error.ToString();
        Assert.Contains("--stage", stderr);
    }

    [Fact]
    public async Task IssueRerunFromStage_AcceptsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "rerun-from-stage", "42", "--stage", "build", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueRerunFromStage_ServerError_SurfacesCodeAndMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(
                    new { success = false, error = "Stage not reached", code = "stage_not_reached" },
                    HttpStatusCode.BadRequest);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "rerun-from-stage", "42", "--stage", "integrate"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        var stderr = error.ToString();
        Assert.Contains("stage_not_reached", stderr);
        Assert.Contains("Stage not reached", stderr);
    }

    [Fact]
    public async Task IssueRerun_NoFromStage_PostsEmptyBodyToRerunEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/rerun")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "rerun", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/rerun", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task IssueRerun_WithFromStage_PostsStageBodyToRerunFromStageEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/rerun-from-stage")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "rerun", "42", "--from-stage", "build"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("build", body["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueRerun_EmptyFromStage_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "rerun", "42", "--from-stage", ""], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--from-stage is required and must not be empty", stderr);
    }

    [Fact]
    public async Task IssueRerun_BlankFromStage_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "rerun", "42", "--from-stage", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--from-stage is required and must not be empty", stderr);
    }

    [Fact]
    public async Task IssueRerun_NoFromStage_HonorsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_xyz/issues/42/rerun")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "rerun", "42", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/rerun", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueRerun_WithFromStage_HonorsProjectIdFlag()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_xyz/issues/42/rerun-from-stage")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "rerun", "42", "--from-stage", "build", "--project", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueRerun_FromStageAlias_PostsSameRequestAsRerunFromStagePeer()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/rerun-from-stage")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var rerunFromStageExit = await MohistCliCommands.RunAsync(
            http,
            ["issue", "rerun-from-stage", "42", "--stage", "build"],
            output, error, fs, executor);

        Assert.Equal(0, rerunFromStageExit);
        var rerunFromStageRequests = handler.Requests.ToList();
        var rerunFromStagePost = rerunFromStageRequests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/rerun-from-stage", rerunFromStagePost.RequestUri?.PathAndQuery);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();

        var rerunExit = await MohistCliCommands.RunAsync(
            http,
            ["issue", "rerun", "42", "--from-stage", "build"],
            output, error, fs, executor);

        var rerunPost = handler.Requests
            .Skip(rerunFromStageRequests.Count)
            .Single(r => r.Method == HttpMethod.Post);

        Assert.Equal(rerunFromStageExit, rerunExit);
        Assert.Equal(rerunFromStagePost.Method, rerunPost.Method);
        Assert.Equal(rerunFromStagePost.RequestUri, rerunPost.RequestUri);
        Assert.Equal(rerunFromStagePost.Body, rerunPost.Body);
    }
}
