using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// T-001 (issue-476): `mo run` is the single command tree for the seven
// state-changing verbs (approve / reject / retry / rerun / pause /
// resume / stop). Every verb targets a run by either a positional
// `<run-id>` or `--issue <number>`. These specs pin:
//   * help shape — the seven verbs are listed, no `rerun-from-stage`
//   * target resolution — mutual exclusion / missing target fail locally
//     (exit 2, no HTTP)
//   * `--issue` resolution — one-shot GET to the issue endpoint reads
//     `workflowRunId`; a missing binding fails with a diagnostic
//     naming the issue
//   * `--message` validation for `reject`
//   * `--from-stage` flag on `rerun`; blank values fail locally
//   * `--yes` confirmation for the irreversible `stop`
//   * server-error surfacing — message + stable code on stderr
//   * `--json` field selection — JSON object with only the requested
//     fields, no `{success,data,error}` wrapper
public class CliRunControlSpecs
{
    private const string WrId = "wr_abc123";

    // ────────────────────────────────────────────────────────────────────
    //  Help shape
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunHelp_ListsAllSevenControlVerbsAndNoRerunFromStageSubcommand()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        foreach (var verb in new[] { "approve", "reject", "retry", "rerun", "pause", "resume", "stop" })
        {
            Assert.Contains($"{verb} ", stdout);
        }
        // `rerun-from-stage` must not exist as a subcommand.
        Assert.DoesNotContain("rerun-from-stage ", stdout);
        Assert.Empty(handler.Requests);
    }

    // ────────────────────────────────────────────────────────────────────
    //  approve — Run ID target
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_WithRunId_PostsToRunScopedEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/approve")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/approve", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Approve_WithRunId_DoesNotResolveProjectOrIssue()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        }, activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/projects/") == true);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/issues/") == true);
    }

    // ────────────────────────────────────────────────────────────────────
    //  approve — --issue target resolution
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_WithIssue_ResolvesBoundRunAndPostsRunScoped()
    {
        const int issueNumber = 42;
        const string boundRunId = "wr_from_issue";

        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/proj_abc/issues/{issueNumber}")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { number = issueNumber, workflowRunId = boundRunId },
                });
            }
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{boundRunId}/approve")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", "--issue", issueNumber.ToString()], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/projects/proj_abc/issues/{issueNumber}");
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{boundRunId}/approve", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Approve_WithIssue_HonorsExplicitProject()
    {
        const int issueNumber = 42;
        const string boundRunId = "wr_proj";
        const string projectRef = "mohist-local";

        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{projectRef}/issues/{issueNumber}")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { number = issueNumber, workflowRunId = boundRunId },
                });
            }
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{boundRunId}/approve")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        }, activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", "--issue", issueNumber.ToString(), "--project", projectRef],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/projects/{projectRef}/issues/{issueNumber}");
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{boundRunId}/approve", postReq.RequestUri?.PathAndQuery);
    }

    // ────────────────────────────────────────────────────────────────────
    //  approve — mutual exclusion / missing target
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_BothRunIdAndIssue_FailsLocallyWithExitTwoAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", WrId, "--issue", "42"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("not both", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approve_NoTarget_FailsLocallyWithExitTwoAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--issue", stderr);
    }

    // ────────────────────────────────────────────────────────────────────
    //  approve — --issue without a bound run
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_IssueWithoutBoundRun_FailsNonZeroNamingIssue()
    {
        const int issueNumber = 99;

        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/proj_abc/issues/{issueNumber}")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { number = issueNumber, workflowRunId = (string?)null },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", "--issue", issueNumber.ToString()], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains($"#{issueNumber}", stderr);
        Assert.Contains("no active workflow run", stderr, StringComparison.OrdinalIgnoreCase);
        // No POST issued.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Approve_IssueThatDoesNotExist_SurfacesServerError()
    {
        const int issueNumber = 404;

        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.JsonError(
                    $"Issue #{issueNumber} not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", "--issue", issueNumber.ToString()], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains($"Issue #{issueNumber} not found", stderr);
        Assert.Contains("not_found", stderr);
    }

    // ────────────────────────────────────────────────────────────────────
    //  approve — server error surfacing
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_ServerError_SurfacesMessageAndCodeOnStderr()
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
            http, ["run", "approve", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Workflow is not active for this run", stderr);
        Assert.Contains("conflict", stderr);
    }

    [Fact]
    public async Task Approve_NotFound_SurfacesServerMessage()
    {
        const string missingRunId = "wr_missing";

        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{missingRunId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", missingRunId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("not found", stderr);
        Assert.Contains("not_found", stderr);
    }

    // ────────────────────────────────────────────────────────────────────
    //  approve — --json field selection
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_WithJsonFieldSelection_ProjectsRequestedFieldsWithoutWrapper()
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
                        workflowRunId = WrId,
                        approved = true,
                        stage = "build",
                        // extra fields that must be filtered out by --json
                        foo = "bar",
                        noise = 42,
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", WrId, "--json", "workflowRunId,approved"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        var parsed = JsonNode.Parse(stdout.Trim()) as JsonObject;
        Assert.NotNull(parsed);
        var keys = parsed!.Select(kv => kv.Key).ToHashSet();
        Assert.Equal(2, keys.Count);
        Assert.Contains("workflowRunId", keys);
        Assert.Contains("approved", keys);
        Assert.Equal(WrId, parsed["workflowRunId"]?.GetValue<string>());
        Assert.True(parsed["approved"]?.GetValue<bool>());
        // No envelope wrappers on stdout.
        Assert.DoesNotContain("\"success\"", stdout);
        Assert.DoesNotContain("\"data\"", stdout);
        Assert.DoesNotContain("\"error\"", stdout);
        // The extra fields from the server response are not leaked.
        Assert.DoesNotContain("\"foo\"", stdout);
        Assert.DoesNotContain("\"noise\"", stdout);
    }

    [Fact]
    public async Task Approve_BareJson_ListsFieldsAndExitsZeroWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "approve", WrId, "--json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The discovery payload lists every available field. We assert on a
        // couple of stable anchors rather than the exact list to avoid
        // pinning cosmetic ordering.
        Assert.Contains("workflowRunId", stdout);
        Assert.Contains("approved", stdout);
        Assert.Empty(handler.Requests);
    }

    // ────────────────────────────────────────────────────────────────────
    //  reject — --message required
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_MissingMessage_FailsLocallyWithExitOneAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "reject", WrId], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--message", error.ToString());
    }

    [Fact]
    public async Task Reject_BlankMessage_FailsLocallyWithExitOneAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "reject", WrId, "--message", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--message", error.ToString());
    }

    [Fact]
    public async Task Reject_WithMessage_PostsWithMessageInBody()
    {
        const string reason = "Rework the auth flow";

        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/reject")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "reject", WrId, "--message", reason], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/reject", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Equal(reason, body!["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task Reject_BothRunIdAndIssue_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "reject", WrId, "--issue", "42", "--message", "anything"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
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
            http, ["run", "reject", WrId, "--message", "anything"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Reject reason is required", stderr);
        Assert.Contains("validation", stderr);
    }

    // ────────────────────────────────────────────────────────────────────
    //  retry
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_PostsToRetryEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/retry")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "retry", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/retry", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Retry_BothRunIdAndIssue_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "retry", WrId, "--issue", "42"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ────────────────────────────────────────────────────────────────────
    //  rerun — with and without --from-stage
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_NoFlag_PostsToRerunEndpointWithEmptyBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/rerun")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "rerun", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/rerun", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task Rerun_WithFromStage_PostsToRerunFromStageWithStageBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/rerun-from-stage")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "rerun", WrId, "--from-stage", "build"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/rerun-from-stage", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!) as JsonObject;
        Assert.Equal("build", body!["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task Rerun_BlankFromStage_FailsLocallyWithExitOneAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "rerun", WrId, "--from-stage", "   "], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--from-stage", error.ToString());
    }

    [Fact]
    public async Task Rerun_EmptyFromStageToken_FailsLocallyWithExitOneAndNoHttp()
    {
        // The `--from-stage ""` case (the parser receives the empty token)
        // must also fail locally. System.CommandLine treats an empty string
        // as a provided-but-empty value, so we keep the same validation
        // path as the whitespace case above.
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "rerun", WrId, "--from-stage", ""], output, error, fs, executor);

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
            http, ["run", "rerun", WrId, "--from-stage", "build"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains(code, stderr);
        Assert.Contains(message, stderr);
    }

    // ────────────────────────────────────────────────────────────────────
    //  pause / resume — reversible, no confirmation
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_PostsToPauseEndpointWithoutRequiringYes()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/pause")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "pause", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/pause", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Resume_PostsToResumeEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/resume")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "resume", WrId], output, error, fs, executor);

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
            http, ["run", "resume", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Workflow is not active for this run", error.ToString());
    }

    // ────────────────────────────────────────────────────────────────────
    //  stop — irreversible, --yes required in non-interactive contexts
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_WithYes_PostsToStopEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/stop")
            {
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "stop", WrId, "--yes"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/workflow-runs/{WrId}/stop", postReq.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Stop_WithoutYesInNonInteractiveMode_FailsWithExitOneAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "stop", WrId], output, error, fs, executor,
            terminalOverride: new CliTerminal(false));

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--yes", stderr);
    }

    [Fact]
    public async Task Stop_WithIssueWithoutYesInNonInteractiveMode_FailsBeforeResolvingIssue()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "stop", "--issue", "42"], output, error, fs, executor,
            terminalOverride: new CliTerminal(false));

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--yes", error.ToString());
    }

    [Fact]
    public async Task Stop_BothRunIdAndIssue_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "stop", WrId, "--issue", "42", "--yes"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stop_HelpExplainsTerminalityAndReferencesPause()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "stop", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // "terminal" or "permanent" must appear; "pause" must be the
        // resumable alternative the help points users to.
        Assert.Contains("terminal", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pause", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Other control verbs do not require --yes
    // ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("retry")]
    [InlineData("rerun")]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task OtherControlVerbs_DoNotRequireYes(string verb)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
                return RecordingHttpHandler.Json(new { success = true, data = new { } });
            return null!;
        });

        var args = verb == "reject"
            ? new[] { "run", verb, WrId, "--message", "reason" }
            : new[] { "run", verb, WrId };

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post);
    }
}
