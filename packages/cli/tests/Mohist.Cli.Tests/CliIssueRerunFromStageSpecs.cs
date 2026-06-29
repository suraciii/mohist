using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueRerunFromStageSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateHarness(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var response = responder?.Invoke(req);
            if (response is not null) return Task.FromResult(response);
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        return (handler, http, output, error, fs, new FakeCommandExecutor());
    }

    [Fact]
    public async Task IssueRerunFromStage_SuccessPath_SendsPostWithStageBody()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
        var (handler, http, output, error, fs, executor) = CreateHarness();

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
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
            ["issue", "rerun-from-stage", "42", "--stage", "build", "--project-id", "proj_xyz"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_xyz/issues/42/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueRerunFromStage_ServerError_SurfacesCodeAndMessage()
    {
        var (handler, http, output, error, fs, executor) = CreateHarness(req =>
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
}
