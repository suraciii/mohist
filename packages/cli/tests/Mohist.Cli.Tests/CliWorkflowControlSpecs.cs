using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliWorkflowControlSpecs
{
    private const string WrId = "wr_abc123";

    [Fact]
    public async Task WorkflowHelp_ExposesControlVerbsAndNoRerunFromStageSubcommand()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        foreach (var verb in new[] { "approve", "reject", "retry", "rerun", "resume", "pause", "stop" })
        {
            Assert.Contains($"{verb} <run-id>", stdout);
        }
        Assert.DoesNotContain("rerun-from-stage <run-id>", stdout);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Approve_SendsPostToRunScopedEndpoint_WithoutProjectOrIssue()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/approve")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "approve", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/approve", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Approve_DoesNotReverseResolveToIssueEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/approve")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "approve", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/issues/") == true);
    }

    [Fact]
    public async Task Approve_ServerNotActive_PrintsMessageAndCodeToStderr()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.JsonError(
                    "Workflow is not active for this run",
                    code: "conflict",
                    statusCode: HttpStatusCode.Conflict);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "approve", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Workflow is not active for this run", stderr);
        Assert.Contains("conflict", stderr);
    }

    [Fact]
    public async Task Reject_MissingMessage_PrintsValidationErrorAndMakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "reject", WrId], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--message", error.ToString());
    }

    [Fact]
    public async Task Reject_WhitespaceMessage_PrintsValidationErrorAndMakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "reject", WrId, "--message", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--message", error.ToString());
    }

    [Fact]
    public async Task Reject_WithMessage_ForwardsReasonInBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/reject")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "reject", WrId, "--message", "Rework the auth flow"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/reject", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Equal("Rework the auth flow", body!["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task Reject_ServerError_SurfacesMessageAndCode()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.JsonError(
                    "Reject reason is required",
                    code: "validation",
                    statusCode: HttpStatusCode.BadRequest);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "reject", WrId, "--message", "anything"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Reject reason is required", stderr);
        Assert.Contains("validation", stderr);
    }

    [Fact]
    public async Task Retry_OnFailedRun_SendsPostToRetryEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/retry")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "retry", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/retry", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Rerun_NoFlag_PostsToRerunEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/rerun")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "rerun", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/rerun", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task Rerun_WithFromStage_PostsToRerunFromStageEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/rerun-from-stage")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "rerun", WrId, "--from-stage", "build"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.Equal("build", body!["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task Rerun_BlankFromStage_RejectsLocallyAndMakesNoRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "rerun", WrId, "--from-stage", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--from-stage", error.ToString());
    }

    [Theory]
    [InlineData("unknown_stage", "Stage 'foo' does not exist")]
    [InlineData("stage_not_reached", "Stage 'integrate' has not been reached")]
    [InlineData("active_work_in_range", "Stage 'build' has active work in range")]
    public async Task Rerun_FromStage_StructuredErrors_AreSurfacedVerbatim(string code, string message)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var status = code == "active_work_in_range"
                    ? HttpStatusCode.Conflict
                    : HttpStatusCode.BadRequest;
                return RecordingHttpHandler.JsonError(message, code, status);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "rerun", WrId, "--from-stage", "build"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains(code, stderr);
        Assert.Contains(message, stderr);
    }

    [Fact]
    public async Task Resume_AfterPause_PostsToResumeEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/resume")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "resume", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/resume", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Resume_AfterStop_ServerRejectsAsNotActive()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.JsonError(
                    "Workflow is not active for this run",
                    code: "conflict",
                    statusCode: HttpStatusCode.Conflict);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "resume", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Workflow is not active for this run", error.ToString());
    }

    [Fact]
    public async Task Pause_SendsPostToPauseEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/pause")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "pause", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/pause", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Stop_SendsPostToStopEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/stop")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "stop", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/stop", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Stop_HelpExplainsTerminalPauseAndForceStop()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "stop", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("stop", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terminal", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pause", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resume", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pause_HelpExplainsResumability()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "pause", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("pause", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resume", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("retry")]
    [InlineData("resume")]
    [InlineData("pause")]
    [InlineData("stop")]
    public async Task ActiveOnly_AndRetry_Verbs_DoNotResolveProject(string verb)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        }, activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", verb, WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/projects/") == true);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/issues/") == true);
    }

    [Fact]
    public async Task Approve_OutputJson_EmitsRawJsonPayload()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { workflowRunId = WrId, approved = true },
                });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "approve", WrId, "--json", "id"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\"success\"", stdout);
        Assert.Contains(WrId, stdout);
    }

    [Fact]
    public async Task Approve_DryRun_PrintsIntendedRequestAndMakesNoHttpCall()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "approve", WrId, "--dry-run"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var stdout = output.ToString();
        Assert.Contains("[dry-run]", stdout);
        Assert.Contains($"/api/workflow-runs/{WrId}/approve", stdout);
    }

    [Fact]
    public async Task Reject_DryRun_PrintsIntendedRequestWithMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "reject", WrId, "--message", "nope", "--dry-run"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var stdout = output.ToString();
        Assert.Contains("[dry-run]", stdout);
        Assert.Contains($"/api/workflow-runs/{WrId}/reject", stdout);
    }

    [Fact]
    public async Task Rerun_DryRun_NoFlag_PrintsRerunPath()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "rerun", WrId, "--dry-run"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var stdout = output.ToString();
        Assert.Contains("[dry-run]", stdout);
        Assert.Contains($"/api/workflow-runs/{WrId}/rerun", stdout);
    }

    [Fact]
    public async Task Rerun_DryRun_WithFromStage_PrintsRerunFromStagePath()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "rerun", WrId, "--from-stage", "build", "--dry-run"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var stdout = output.ToString();
        Assert.Contains("[dry-run]", stdout);
        Assert.Contains($"/api/workflow-runs/{WrId}/rerun-from-stage", stdout);
        Assert.Contains("build", stdout);
    }

    [Fact]
    public async Task Approve_NotFound_PrintsErrorAndExitsWithFour()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.JsonError(
                    "Workflow run 'wr_missing' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "approve", "wr_missing"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString());
    }

    // ────────────────────────────────────────────────────────────
    //  Issue shortcut regression — ensure existing CLI behavior is intact
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueApprove_Regression_StillHitsIssueScopedEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/approve")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "approve", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/approve", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueRerunFromStage_Regression_StillHitsIssueScopedEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/rerun-from-stage")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "rerun-from-stage", "42", "--stage", "build"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.Equal("build", body!["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueReject_Regression_StillHitsIssueScopedEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/reject")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "reject", "42", "--message", "rework"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/reject", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueForceStop_Regression_StillHitsIssueScopedEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues/42/force-stop")
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "force-stop", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues/42/force-stop", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkflowList_IsNoLongerExposed()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workflow", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}