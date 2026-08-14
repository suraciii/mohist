using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

[CollectionDefinition("RunReads", DisableParallelization = true)]
public sealed class RunReadsCollectionDefinition
{
}

// T-002 (issue-476): `mo run list` / `mo run view` / `mo run watch`.
// These reads share the same target-resolution contract as the control
// verbs (Run ID or `--issue <number>`, never both; usage failure → exit 2
// with no HTTP). The list derivation pulls from the issues list and
// projects to { id, status, stage, currentStage, issueNumber }. The view
// reuses the existing WorkflowRunDetail shape with --yaml / --json mutually
// exclusive. The watch polls the run detail endpoint, prints NDJSON lines
// on status / stage changes, and exits 0 when the run reaches a terminal
// status. All watch timing is driven by an injected TimeProvider so tests
// never touch wall-clock (design/testing.md).
[Collection("RunReads")]
public class CliRunReadsSpecs
{
    private const string WrId = "wr_read01";
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private static object SampleRunDetail(
        string id = WrId,
        string status = "running",
        string? currentStage = "build",
        object? agentResultAttention = null,
        string? agentAction = "mohist/pi",
        string? agentRuntime = "pi") => new
    {
        status = new
        {
            workflowRunId = id,
            status,
            currentStage,
            assignedTo = (string?)null,
            stages = new[]
            {
                new
                {
                    stage = "plan",
                    status = "completed",
                    order = 0,
                    tasks = Array.Empty<object>(),
                    checks = Array.Empty<object>(),
                    approvalStatus = (object?)null,
                    failure = (object?)null,
                },
                new
                {
                    stage = currentStage ?? "build",
                    status = status == "completed" ? "completed" : "running",
                    order = 1,
                    tasks = Array.Empty<object>(),
                    checks = Array.Empty<object>(),
                    approvalStatus = (object?)null,
                    failure = (object?)null,
                },
            },
            pendingWork = (object?)null,
            failure = (object?)null,
            availableActions = Array.Empty<object>(),
            agentResultAttention,
            metadata = new { name = "Mohist", labels = new Dictionary<string, string>(), createdAt = "2026-07-05T00:00:00Z" },
        },
        issueRef = new { projectId = "proj_abc", number = 42, title = "Close the agent subscriptions gap" },
        workflowProfileId = "mohist/github-pr",
        agentAction,
        agentRuntime,
    };

    private static object SampleIssue(int number, string workflowRunId, string workflowStatus, string workflowStage) => new
    {
        number,
        title = $"Issue #{number}",
        workflowRunId,
        workflowStatus,
        workflowStage,
        workflowProfileId = "mohist/github-pr",
        status = "in_progress",
        priority = "p2",
    };

    // ────────────────────────────────────────────────────────────────────
    //  help shape
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunHelp_ListsAllReadSubcommands()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        foreach (var verb in new[] { "list", "view", "watch" })
        {
            Assert.Contains($"{verb} ", stdout);
        }
        // The reads share the seven control verbs on the same `run` root —
        // confirm none of the legacy workflow reads (`status`, `events`,
        // `list-sessions`, `variables`) leaked into the new help text.
        var subcommandLines = stdout
            .Split('\n')
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();
        foreach (var verb in new[] { "status", "events", "list-sessions", "variables" })
        {
            Assert.DoesNotContain(subcommandLines, line => line.TrimStart().StartsWith($"{verb} ", StringComparison.Ordinal));
        }
        Assert.Empty(handler.Requests);
    }

    // ────────────────────────────────────────────────────────────────────
    //  list — derived from issues list
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_DerivesFromIssuesListAndProjectsRuns()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues")
            {
                var issues = new[]
                {
                    SampleIssue(42, "wr_alpha", "running", "build"),
                    SampleIssue(43, "wr_beta", "completed", "integrate"),
                };
                return RecordingHttpHandler.Json(new { success = true, data = issues });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/projects/proj_abc/issues", getReq.RequestUri?.PathAndQuery);

        var stdout = output.ToString();
        Assert.Contains("run id", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wr_alpha", stdout);
        Assert.Contains("wr_beta", stdout);
        Assert.Contains("running", stdout);
        Assert.Contains("completed", stdout);
        Assert.Contains("#42", stdout);
        Assert.Contains("#43", stdout);
    }

    [Fact]
    public async Task List_FiltersOutIssuesWithoutWorkflowRun()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues")
            {
                var issues = new object[]
                {
                    SampleIssue(42, "wr_alpha", "running", "build"),
                    new
                    {
                        number = 43,
                        title = "Issue #43",
                        workflowRunId = (string?)null,
                        workflowStatus = (string?)null,
                        workflowStage = (string?)null,
                        workflowProfileId = "mohist/github-pr",
                        status = "backlog",
                        priority = "p2",
                    },
                };
                return RecordingHttpHandler.Json(new { success = true, data = issues });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("wr_alpha", stdout);
        Assert.DoesNotContain("wr_missing", stdout);
    }

    [Fact]
    public async Task List_NoRunsInProject_ExitsZeroWithEmptyMessage()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues")
            {
                return RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("No workflow runs", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_WithJsonFieldSelection_ProjectsRequestedFieldsOnly()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/issues")
            {
                var issues = new[]
                {
                    SampleIssue(42, "wr_alpha", "running", "build"),
                    SampleIssue(43, "wr_beta", "completed", "integrate"),
                };
                return RecordingHttpHandler.Json(new { success = true, data = issues });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "list", "--json", "id,status,currentStage"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        var arr = JsonNode.Parse(stdout.Trim()) as JsonArray;
        Assert.NotNull(arr);
        Assert.Equal(2, arr!.Count);

        var expectedKeys = new HashSet<string> { "id", "status", "currentStage" };
        foreach (var item in arr)
        {
            var obj = Assert.IsType<JsonObject>(item);
            Assert.Equal(expectedKeys, obj.Select(kv => kv.Key).ToHashSet());
        }

        var first = (JsonObject)arr[0]!;
        Assert.Equal("wr_alpha", first["id"]?.GetValue<string>());
        Assert.Equal("running", first["status"]?.GetValue<string>());
        Assert.Equal("build", first["currentStage"]?.GetValue<string>());
        // No envelope wrappers on stdout.
        Assert.DoesNotContain("\"success\"", stdout);
        Assert.DoesNotContain("\"data\"", stdout);
        Assert.DoesNotContain("\"error\"", stdout);
    }

    [Fact]
    public async Task List_BareJson_ListsFieldsAndExitsZeroWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "list", "--json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("id", stdout);
        Assert.Contains("status", stdout);
        Assert.Contains("currentStage", stdout);
        Assert.Contains("issueNumber", stdout);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task List_HonorsExplicitProject()
    {
        const string projectRef = "mohist-local";
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/projects/{projectRef}/issues")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { SampleIssue(99, "wr_gamma", "running", "check") },
                });
            }
            return null!;
        }, activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "list", "--project", projectRef],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/projects/{projectRef}/issues");
    }

    [Fact]
    public async Task List_NoActiveProject_PrintsMessageAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("project", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  view — Run ID target
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task View_WithRunId_GetsRunDetailAndRendersIt()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
            {
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail() });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("run id:", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status:", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("current stage:", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent action:  mohist/pi", stdout, StringComparison.Ordinal);
        Assert.Contains("agent runtime: pi", stdout, StringComparison.Ordinal);
        Assert.Contains("issue:", stdout, StringComparison.OrdinalIgnoreCase);
        // Stages are rendered as a sub-table; the renderer emits a "stage"
        // header inside the run detail view (RenderWorkflowRunStages).
        Assert.Contains("stage", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approval", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task View_BlockedResultRendersAttentionWithoutFailure()
    {
        var attention = new
        {
            state = "blocked",
            reason = "agent-result-unconfirmed",
            message = "Runner disconnected before the Agent result was accepted.",
            deadlineAt = "2026-08-14T11:01:58Z",
            taskRunId = "build.1",
            workId = "build.1",
            runnerId = "runner-pluto",
            agentSessionId = "session-1",
            agentTurnId = "turn-1",
            nextAction = "Restore the original Runner and allow the result to replay.",
            recoveryActions = new[] { "stop" },
        };
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(WrId, "blocked", "build", attention) });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(http, ["run", "view", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("status:        blocked", stdout);
        Assert.Contains("agent result attention:", stdout);
        Assert.Contains("agent-result-unconfirmed", stdout);
        Assert.Contains("session-1", stdout);
        Assert.Contains("turn-1", stdout);
        Assert.DoesNotContain("failure:", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task View_WithRunId_DoesNotResolveIssue()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
            {
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail() });
            }
            return null!;
        }, activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/issues/") == true);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri?.PathAndQuery.Contains("/projects/") == true);
    }

    [Fact]
    public async Task View_WithJsonFieldSelection_ProjectsRunFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
            {
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail() });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["run", "view", WrId, "--json", "id,status,currentStage,agentAction,agentRuntime"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var selected = Assert.IsType<JsonObject>(JsonNode.Parse(output.ToString()));
        Assert.Equal(
            new HashSet<string> { "id", "status", "currentStage", "agentAction", "agentRuntime" },
            selected.Select(property => property.Key).ToHashSet());
        Assert.Equal(WrId, selected["id"]?.GetValue<string>());
        Assert.Equal("running", selected["status"]?.GetValue<string>());
        Assert.Equal("build", selected["currentStage"]?.GetValue<string>());
        Assert.Equal("mohist/pi", selected["agentAction"]?.GetValue<string>());
        Assert.Equal("pi", selected["agentRuntime"]?.GetValue<string>());
    }

    [Fact]
    public async Task View_NullAgentBinding_RemainsVisibleInTable()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}"
                ? RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = SampleRunDetail(agentAction: null, agentRuntime: null),
                })
                : null!);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("profile:       mohist/github-pr", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent action:  (none)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent runtime: (none)", output.ToString(), StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────
    //  view — --issue target resolution
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task View_WithIssue_ResolvesBoundRunAndGetsDetail()
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
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{boundRunId}")
            {
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(boundRunId) });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", "--issue", issueNumber.ToString()],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/projects/proj_abc/issues/{issueNumber}");
        var detailReq = handler.Requests.Last(r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/workflow-runs/{boundRunId}");
        Assert.NotNull(detailReq);
    }

    [Fact]
    public async Task View_WithIssueWithoutBoundRun_FailsNonZeroNamingIssue()
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
            http, ["run", "view", "--issue", issueNumber.ToString()],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains($"#{issueNumber}", stderr);
        Assert.Contains("no active workflow run", stderr, StringComparison.OrdinalIgnoreCase);
        // Only the issue GET was issued — no /api/workflow-runs/{id} call.
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery.StartsWith("/api/workflow-runs/", StringComparison.Ordinal) == true);
    }

    // ────────────────────────────────────────────────────────────────────
    //  view — mutual exclusion / missing target
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task View_BothRunIdAndIssue_FailsLocallyWithExitTwoAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId, "--issue", "42"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("not both", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task View_NoTarget_FailsLocallyWithExitTwoAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--issue", stderr);
    }

    // ────────────────────────────────────────────────────────────────────
    //  view — --yaml and --json mutual exclusion
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task View_WithYaml_GetsYamlSubresourceAndPrintsDefinition()
    {
        const string yamlBody = "name: mohist/local\nstages:\n  - id: plan\n  - id: build";
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/yaml")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { workflowRunId = WrId, yaml = yamlBody },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId, "--yaml"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.Equal($"/api/workflow-runs/{WrId}/yaml", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Equal(yamlBody, stdout.TrimEnd());
    }

    [Fact]
    public async Task View_WithYaml_DoesNotHitBareRunDetailEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}/yaml")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { workflowRunId = WrId, yaml = "name: mohist/local\n" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId, "--yaml"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}");
    }

    [Fact]
    public async Task View_WithYamlAndJson_FailsLocallyWithExitTwoAndNoHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId, "--yaml", "--json", "id"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--yaml", stderr);
        Assert.Contains("--json", stderr);
    }

    [Fact]
    public async Task View_WithIssueResolutionAndYaml_GetsYamlSubresource()
    {
        const int issueNumber = 42;
        const string boundRunId = "wr_yaml_target";

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
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{boundRunId}/yaml")
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { workflowRunId = boundRunId, yaml = "name: mohist/local\n" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", "--issue", issueNumber.ToString(), "--yaml"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r =>
            r.Method == HttpMethod.Get
            && r.RequestUri?.PathAndQuery == $"/api/workflow-runs/{boundRunId}/yaml");
    }

    [Fact]
    public async Task View_UnknownRunId_SurfacesServerError()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "view", WrId], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("not found", stderr);
        Assert.Contains("not_found", stderr);
    }

    // ────────────────────────────────────────────────────────────────────
    //  watch — polling loop, FakeTimeProvider, NDJSON on change
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Watch_PollsUntilTerminalStatus_ExitsZeroAndEmitsNdjson()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pollCount = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
            {
                pollCount++;
                var (status, stage) = pollCount switch
                {
                    1 => ("running", "plan"),
                    2 => ("running", "build"),
                    _ => ("completed", "integrate"),
                };
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(WrId, status, stage) });
            }
            return null!;
        });

        var watchTask = MohistCliCommands.RunAsync(
            http, ["run", "watch", WrId], output, error, fs, executor,
            timeProvider: time);

        // Wait for first poll, advance time → second poll fires.
        await handler.WaitForRequestCountAsync(1);
        await Task.Run(static () => { });
        time.Advance(DefaultInterval);

        // Wait for second poll, advance time → third poll (terminal) fires.
        await handler.WaitForRequestCountAsync(2);
        await Task.Run(static () => { });
        time.Advance(DefaultInterval);

        var exitCode = await watchTask;
        Assert.Equal(0, exitCode);

        Assert.Equal(3, pollCount);
        var stdout = output.ToString();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        var first = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(first);
        Assert.Equal(WrId, first!["id"]?.GetValue<string>());
        Assert.Equal("running", first["status"]?.GetValue<string>());
        Assert.Equal("plan", first["stage"]?.GetValue<string>());

        var second = JsonNode.Parse(lines[1]) as JsonObject;
        Assert.NotNull(second);
        Assert.Equal("running", second!["status"]?.GetValue<string>());
        Assert.Equal("build", second["stage"]?.GetValue<string>());

        var third = JsonNode.Parse(lines[2]) as JsonObject;
        Assert.NotNull(third);
        Assert.Equal("completed", third!["status"]?.GetValue<string>());
        Assert.Equal("integrate", third["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task Watch_BlockedResultContinuesUntilLateAuthoritativeResult()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pollCount = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method != HttpMethod.Get || req.RequestUri?.PathAndQuery != $"/api/workflow-runs/{WrId}")
                return null!;
            pollCount++;
            var status = pollCount == 1 ? "blocked" : "completed";
            return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(WrId, status, "build") });
        });

        var watchTask = MohistCliCommands.RunAsync(
            http, ["run", "watch", WrId], output, error, fs, executor, timeProvider: time);
        await handler.WaitForRequestCountAsync(1);
        await Task.Run(static () => { });
        time.Advance(DefaultInterval);

        var exitCode = await watchTask;

        Assert.Equal(0, exitCode);
        Assert.Equal(2, pollCount);
        Assert.Contains("\"status\":\"blocked\"", output.ToString());
        Assert.Contains("\"status\":\"completed\"", output.ToString());
    }

    [Fact]
    public async Task Watch_DoesNotEmitWhenSnapshotUnchanged()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pollCount = 0;
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
            {
                pollCount++;
                // First three polls return the same status / stage. The
                // fourth poll flips to a terminal status to exit the loop.
                var status = pollCount < 4 ? "running" : "completed";
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(WrId, status, "build") });
            }
            return null!;
        });

        var watchTask = MohistCliCommands.RunAsync(
            http, ["run", "watch", WrId], output, error, fs, executor,
            timeProvider: time);

        // Drive the loop forward: three identical polls, then a terminal one.
        for (var i = 0; i < 3; i++)
        {
            await handler.WaitForRequestCountAsync(i + 1);
            await Task.Run(static () => { });
            time.Advance(DefaultInterval);
        }
        await handler.WaitForRequestCountAsync(4);

        var exitCode = await watchTask;
        Assert.Equal(0, exitCode);

        // Only the initial snapshot and the terminal transition emit.
        var lines = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var terminal = JsonNode.Parse(lines[1]) as JsonObject;
        Assert.Equal("completed", terminal!["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task Watch_WithIssue_ResolvesBoundRunAndPolls()
    {
        const int issueNumber = 42;
        const string boundRunId = "wr_from_issue_watch";

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pollCount = 0;
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
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{boundRunId}")
            {
                pollCount++;
                var status = pollCount == 1 ? "running" : "completed";
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(boundRunId, status, "build") });
            }
            return null!;
        });

        var watchTask = MohistCliCommands.RunAsync(
            http, ["run", "watch", "--issue", issueNumber.ToString()],
            output, error, fs, executor,
            timeProvider: time);

        await handler.WaitForRequestCountAsync(2); // issue GET + first poll
        await Task.Run(static () => { });
        time.Advance(DefaultInterval);
        var exitCode = await watchTask;
        Assert.Equal(0, exitCode);
        Assert.Equal(2, pollCount);
    }

    [Fact]
    public async Task Watch_BothRunIdAndIssue_FailsLocallyWithExitTwoAndNoHttp()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "watch", WrId, "--issue", "42"],
            output, error, fs, executor,
            timeProvider: time);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("not both", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_NoTarget_FailsLocallyWithExitTwoAndNoHttp()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "watch"], output, error, fs, executor,
            timeProvider: time);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        var stderr = error.ToString();
        Assert.Contains("--issue", stderr);
    }

    [Fact]
    public async Task Watch_UnknownRunId_SurfacesServerErrorAndStopsPolling()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
            {
                return RecordingHttpHandler.JsonError(
                    $"Workflow run '{WrId}' not found",
                    code: "not_found",
                    statusCode: HttpStatusCode.NotFound);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["run", "watch", WrId], output, error, fs, executor,
            timeProvider: time);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("not found", stderr);
        Assert.Contains("not_found", stderr);
        // The polling loop must NOT have made a second request after the
        // first one surfaced the server error.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Watch_IntervalOverride_UsesProvidedValue()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pollCount = 0;
        var customInterval = TimeSpan.FromMilliseconds(250);
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get
                && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
            {
                pollCount++;
                var status = pollCount == 1 ? "running" : "completed";
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(WrId, status, "build") });
            }
            return null!;
        });

        var watchTask = MohistCliCommands.RunAsync(
            http, ["run", "watch", WrId, "--interval", "250"],
            output, error, fs, executor,
            timeProvider: time);

        await handler.WaitForRequestCountAsync(1);
        // Advance by the custom interval — anything smaller would leave the
        // Task.Delay in-flight, anything larger would skip ahead.
        await Task.Run(static () => { });
        time.Advance(customInterval);
        var exitCode = await watchTask;
        Assert.Equal(0, exitCode);
        Assert.Equal(2, pollCount);
    }
}
