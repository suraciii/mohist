using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliRunFeedbackSpecs
{
    private static object Feedback(string id, string stage, string body) => new
    {
        id,
        issueNumber = 42,
        workflowRunId = "wr_abc",
        stage,
        status = "open",
        body,
        createdAt = "2026-07-22T10:00:00Z",
        updatedAt = "2026-07-22T10:00:00Z",
    };

    private static object RunDetail() => new
    {
        status = new { workflowRunId = "wr_abc", status = "running", currentStage = "plan" },
        issueRef = new { projectId = "proj_abc", number = 42, title = "Test issue" },
    };

    [Fact]
    public async Task List_ByRunId_ReadsIssueScopedFeedback()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/workflow-runs/wr_abc"
                ? RecordingHttpHandler.Json(new { success = true, data = RunDetail() })
                : req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback"
                    ? RecordingHttpHandler.Json(new { success = true, data = new[] { Feedback("fb_001", "plan", "Review") } })
                    : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "list", "wr_abc"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["/api/workflow-runs/wr_abc", "/api/projects/proj_abc/issues/42/feedback"],
            handler.Requests.Select(request => request.RequestUri!.PathAndQuery));
        Assert.Contains("fb_001", output.ToString());
    }

    [Fact]
    public async Task List_ByIssue_ResolvesRunAndReadsFeedback()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42"
                ? RecordingHttpHandler.Json(new { success = true, data = new { workflowRunId = "wr_abc" } })
                : req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback"
                    ? RecordingHttpHandler.Json(new { success = true, data = new[] { Feedback("fb_001", "plan", "Review") } })
                    : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "list", "--issue", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, request =>
            request.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback");
    }

    [Fact]
    public async Task List_WithStage_AddsStageFilter()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/workflow-runs/wr_abc"
                ? RecordingHttpHandler.Json(new { success = true, data = RunDetail() })
                : req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback?stage=plan"
                    ? RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })
                    : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "list", "wr_abc", "--stage", "plan"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_abc/issues/42/feedback?stage=plan",
            handler.Requests.Last().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task View_ByFeedbackId_ReadsRecord()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/workflow-runs/wr_abc"
                ? RecordingHttpHandler.Json(new { success = true, data = RunDetail() })
                : req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback/fb_001"
                    ? RecordingHttpHandler.Json(new { success = true, data = Feedback("fb_001", "plan", "Review") })
                    : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "view", "wr_abc", "--feedback", "fb_001"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_abc/issues/42/feedback/fb_001",
            handler.Requests.Last().RequestUri!.PathAndQuery);
        Assert.Contains("fb_001", output.ToString());
    }

    [Fact]
    public async Task View_LatestWithStage_ListsThenReadsMostRecentRecord()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/workflow-runs/wr_abc"
                ? RecordingHttpHandler.Json(new { success = true, data = RunDetail() })
                : req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback?stage=check"
                    ? RecordingHttpHandler.Json(new { success = true, data = new[] { Feedback("fb_002", "check", "Ship it") } })
                    : req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback/fb_002"
                        ? RecordingHttpHandler.Json(new { success = true, data = Feedback("fb_002", "check", "Ship it") })
                        : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "view", "wr_abc", "--latest", "--stage", "check"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_abc/issues/42/feedback/fb_002",
            handler.Requests.Last().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task View_WithoutSelector_FailsBeforeHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "view", "wr_abc"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--feedback <id> or --latest", error.ToString());
    }

    [Fact]
    public async Task List_IssueWithoutRun_ReportsIssueNumber()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/99"
                ? RecordingHttpHandler.Json(new { success = true, data = new { workflowRunId = (string?)null } })
                : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "list", "--issue", "99"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Issue #99 has no active workflow run", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task List_SelectedJson_ProjectsFeedbackFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/workflow-runs/wr_abc"
                ? RecordingHttpHandler.Json(new { success = true, data = RunDetail() })
                : req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/feedback"
                    ? RecordingHttpHandler.Json(new { success = true, data = new[] { Feedback("fb_001", "plan", "Review") } })
                    : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "feedback", "list", "wr_abc", "--json", "id,stage,body"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var record = JsonNode.Parse(output.ToString())![0]!;
        Assert.Equal("fb_001", record["id"]?.GetValue<string>());
        Assert.Null(record["status"]);
    }
}
