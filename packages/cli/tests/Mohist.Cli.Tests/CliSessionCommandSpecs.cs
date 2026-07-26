using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for the top-level `mo session` command group (issue-479 T-005 /
// design D5+D6). The group is source-agnostic: every verb is addressed by
// the stable AgentSession id regardless of whether the session originated
// from an Agent launch (agent-launch source) or a Workflow run
// (workflow source). `list` takes --agent / --issue / --run as filters.
public class CliSessionCommandSpecs
{
    private const string ActiveProjectId = "proj_test";
    private const string StableSessionId = "sess_123";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, ActiveProjectId);
        return (http, handler, output, error, fs, executor);
    }

    // ----- help / command tree -----

    [Fact]
    public async Task SessionHelp_ListsAllSubcommands()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("view", stdout, StringComparison.Ordinal);
        Assert.Contains("transcript", stdout, StringComparison.Ordinal);
        Assert.Contains("compact", stdout, StringComparison.Ordinal);
        Assert.Contains("reset", stdout, StringComparison.Ordinal);
        Assert.Contains("followup", stdout, StringComparison.Ordinal);
        Assert.Contains("cancel", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("show", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("ls", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFollowupHelp_ListsTextInputFlags()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--text", stdout, StringComparison.Ordinal);
        Assert.Contains("--text-file", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--text-stdin", stdout, StringComparison.Ordinal);
        Assert.Contains("joins an active turn", stdout, StringComparison.Ordinal);
        Assert.Contains("user-initiated turn when idle", stdout, StringComparison.Ordinal);
        Assert.Contains("without creating a TaskRun or AgentJob", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentHelp_DoesNotListSessionSubgroup()
    {
        // The retired `mo agent session` subgroup (issue-479 T-005) is gone.
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("launch", stdout, StringComparison.Ordinal);
        Assert.Contains("job", stdout, StringComparison.Ordinal);
        // The `agent` group's own description mentions "session" as a noun;
        // assert against the subcommand entry line shape rather than the
        // bare word so the description text does not produce a false
        // negative.
        Assert.DoesNotContain("Manage a generic AgentSession", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentSessionSubgroup_CommandNotFound()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for a parse error"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "view", StableSessionId], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueSessionSubgroup_CommandNotFound()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for a parse error"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "view", "42", "plan"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueSessionsList_CommandNotFound()
    {
        // `mo issue sessions <num>` was retired; replaced by `mo session list --issue <num>`.
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for a parse error"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "sessions", "42"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ----- show by id (both sources) -----

    [Fact]
    public async Task SessionShow_AgentLaunchSource_RendersAgentIdentity()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = StableSessionId,
                    source = "agent-launch",
                    activity = "idle",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    workflowRunId = (string?)null,
                    sessionName = (string?)null,
                    createdAt = "2026-06-26T10:00:00Z",
                    lastActivityAt = "2026-06-26T10:05:00Z",
                    model = "gpt-5",
                    contextRefs = (object?)null,
                    usage = new { totalTokens = 3210, inputTokens = 2000, outputTokens = 1210, costAmount = 0.05, costCurrency = "USD" },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions/{StableSessionId}", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains($"session id:     {StableSessionId}", stdout, StringComparison.Ordinal);
        Assert.Contains("source:         agent-launch", stdout, StringComparison.Ordinal);
        Assert.Contains("agent:          agent_456 (reviewer)", stdout, StringComparison.Ordinal);
        Assert.Contains("activity:       idle", stdout, StringComparison.Ordinal);
        Assert.Contains("model:          gpt-5", stdout, StringComparison.Ordinal);
        Assert.Contains("tokens:         3210 (input 2000, output 1210)", stdout, StringComparison.Ordinal);
        Assert.Contains("cost:           0.05 USD", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow run", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_WorkflowSource_RendersWorkflowIdentity()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_wf_1",
                    source = "workflow",
                    activity = "idle",
                    agentId = (string?)null,
                    agentName = (string?)null,
                    workflowRunId = "run_42",
                    sessionName = "plan",
                    createdAt = "2026-06-26T09:00:00Z",
                    lastActivityAt = "2026-06-26T09:30:00Z",
                    model = "gpt-5",
                    contextRefs = new { issueNumber = 7, epicNumber = (int?)null, repository = (string?)null, workspacePath = (string?)null },
                    usage = new { totalTokens = 1024, inputTokens = 512, outputTokens = 512, costAmount = (double?)null, costCurrency = (string?)null },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", "sess_wf_1"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions/sess_wf_1", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("source:         workflow", stdout, StringComparison.Ordinal);
        Assert.Contains("workflow run:   run_42", stdout, StringComparison.Ordinal);
        Assert.Contains("session name:   plan", stdout, StringComparison.Ordinal);
        Assert.Contains("issue #7", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("agent:          ", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_NotFound_SurfacesServerError()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                $"Session {StableSessionId} not found",
                "session_not_found",
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionShow_ProjectOverride_UsesProjectArgument()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = StableSessionId, source = "agent-launch", activity = "idle" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", StableSessionId, "--project", "proj_other"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/proj_other/sessions/{StableSessionId}", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionShow_SelectedJson_ProjectsRequestedFields()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = StableSessionId,
                    source = "agent-launch",
                    activity = "idle",
                    agentId = "agent_456",
                    agentName = "reviewer",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", StableSessionId, "--json", "id,source,agentName"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"id\":", stdout, StringComparison.Ordinal);
        Assert.Contains("\"source\":", stdout, StringComparison.Ordinal);
        Assert.Contains("\"agentName\":", stdout, StringComparison.Ordinal);
    }

    // ----- transcript by id (both sources) -----

    [Fact]
    public async Task SessionTranscript_AgentLaunchSource_HitsUnifiedRouteAndRendersSummary()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    turns = new[]
                    {
                        new { id = "turn_1", startedAt = "2026-06-26T10:00:00Z" },
                        new { id = "turn_2", startedAt = "2026-06-26T10:05:00Z" },
                    },
                    partCount = 4,
                    lastActivityAt = "2026-06-26T10:05:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "transcript", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions/{StableSessionId}/transcript", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("turns:          2", stdout, StringComparison.Ordinal);
        Assert.Contains("parts:          4", stdout, StringComparison.Ordinal);
        Assert.Contains("first activity: 2026-06-26T10:00:00Z", stdout, StringComparison.Ordinal);
        Assert.Contains("last activity:  2026-06-26T10:05:00Z", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionTranscript_WorkflowSource_HitsUnifiedRoute()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    turns = new[] { new { id = "turn_1", startedAt = "2026-06-26T09:00:00Z" } },
                    partCount = 2,
                    lastActivityAt = "2026-06-26T09:05:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "transcript", "sess_wf_1"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions/sess_wf_1/transcript", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionTranscript_NotFound_SurfacesServerError()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                $"Session {StableSessionId} not found",
                "session_not_found",
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "transcript", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionTranscript_Json_EmitsFullTranscript()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    turns = new[]
                    {
                        new { id = "turn_1", startedAt = "2026-06-26T10:00:00Z", assistant = new[] { new { type = "text", text = "first message body" } } },
                    },
                    partCount = 1,
                    lastActivityAt = "2026-06-26T10:00:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "transcript", StableSessionId, "--json", "partCount,turns"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"partCount\": 1", stdout, StringComparison.Ordinal);
        Assert.Contains("\"text\": \"first message body\"", stdout, StringComparison.Ordinal);
    }

    // ----- list by each filter -----

    [Fact]
    public async Task SessionList_AgentFilter_DelegatesToUnifiedRouteWithAgent()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.Contains("/sessions?", StringComparison.Ordinal) && path.Contains("agent=reviewer", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new { id = "sess_1", source = "agent-launch", agentId = "agent_123", agentName = "reviewer", activity = "running", lastActivityAt = "2026-06-26T10:05:00Z" },
                    },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.JsonError("unexpected path: " + path, null, HttpStatusCode.NotFound));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--agent", "reviewer"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        // The server does the agent-ref resolution internally; the CLI
        // sends a single GET against the unified list route with the
        // agent name as the query parameter.
        var listRequest = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, listRequest.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions?agent=reviewer", listRequest.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("sess_1", stdout, StringComparison.Ordinal);
        Assert.Contains("agent-launch", stdout, StringComparison.Ordinal);
        Assert.Contains("reviewer", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_IssueFilter_DelegatesToUnifiedRouteWithIssue()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { id = "sess_wf_1", source = "workflow", workflowRunId = "run_42", sessionName = "plan", activity = "idle", lastActivityAt = "2026-06-26T09:30:00Z" },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--issue", "42"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var listRequest = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions?issue=42", listRequest.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("sess_wf_1", stdout, StringComparison.Ordinal);
        Assert.Contains("workflow", stdout, StringComparison.Ordinal);
        Assert.Contains("run_42/plan", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_RunFilter_DelegatesToUnifiedRouteWithRun()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { id = "sess_wf_1", source = "workflow", workflowRunId = "run_42", sessionName = "plan", activity = "idle", lastActivityAt = "2026-06-26T09:30:00Z" },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--run", "run_42"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions?run=run_42", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionList_NoFilter_RejectsWithScopedUsageFailure()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when no filter is provided"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list"], output, error, fileSystem, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("--agent, --issue, or --run is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo session list", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionList_MultipleFilters_RejectsWithScopedUsageFailure()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when filters conflict"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--agent", "reviewer", "--issue", "42"], output, error, fileSystem, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("Only one of", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo session list", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionList_EmptyResult_RendersEmptyNotice()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--issue", "42"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("No sessions", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_SelectedJson_ProjectsRequestedFields()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { id = "sess_1", source = "agent-launch", agentId = "agent_123", agentName = "reviewer", activity = "running" },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--agent", "reviewer", "--json", "id,source"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"id\":", stdout, StringComparison.Ordinal);
        Assert.Contains("\"source\": \"agent-launch\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_LimitForwardedAsQuery()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--issue", "42", "--limit", "5"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions?issue=42&limit=5", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    // ----- followup (no job) -----

    [Fact]
    public async Task SessionFollowup_Table_PostsFollowupAndPrintsDeliveryStatus()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text", "add a logout route"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/agent-sessions/{StableSessionId}/followup", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("add a logout route", body["text"]?.GetValue<string>());
        Assert.Contains("delivery: sent", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFollowup_DoesNotTouchAgentJobRoutes()
    {
        // The followup action must hit the session followup route, not
        // any AgentJob or task-run route — the job owner remains the
        // sole terminal authority (issue-479 D6).
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text", "ping"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var path = handler.Requests.Single().RequestUri?.PathAndQuery ?? string.Empty;
        Assert.DoesNotContain("/agent-jobs/", path, StringComparison.Ordinal);
        Assert.DoesNotContain("/jobs/", path, StringComparison.Ordinal);
        Assert.DoesNotContain("/dispatch", path, StringComparison.Ordinal);
        Assert.DoesNotContain("/runs", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFollowup_TextFile_ReadsFileAndSendsContents()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));
        var path = "/tmp/note.md";
        fileSystem.AddFile(path, "refactor this loop");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text-file", path], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!.AsObject();
        Assert.Equal("refactor this loop", body["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionFollowup_TextFileDash_ReadsFromStdin()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));

        var stdin = new StringReader("please continue");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text-file", "-"], output, error, fileSystem, executor,
            standardInput: stdin);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!.AsObject();
        Assert.Equal("please continue", body["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionFollowup_SelectedJson_ProjectsStatus()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text", "Hi", "--json", "status"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"status\": \"sent\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFollowup_MissingAllTextSources_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("text is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_BlankInlineTextWithFile_StillFailsMutualExclusion()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        fileSystem.AddFile("/tmp/t", "from file");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text", "", "--text-file", "/tmp/t"], output, error, fileSystem, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_TextStdin_IsRejectedAsUsageFailure()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text-stdin"], output, error, fileSystem, executor,
            standardInput: new StringReader("from stdin"));

        Assert.Equal(2, exitCode);
        Assert.Contains("--text-stdin", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_UnknownSession_SurfacesServerError()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                $"Session sess_missing not found", "session_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", "sess_missing", "--text", "Hi"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionFollowup_TerminalSession_SurfacesConflict()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session is no longer active", "session_inactive", HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "followup", StableSessionId, "--text", "Hi"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Session is no longer active", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    // ----- cancel (runtime-only, no job rewrite) -----

    [Fact]
    public async Task SessionCancel_Table_PostsCancelAndPrintsState()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "cancelled" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/agent-sessions/{StableSessionId}/cancel", request.RequestUri?.PathAndQuery);
        Assert.Contains("state: cancelled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_OnlyHitsCancelRoute_DoesNotTouchAgentJobRoutes()
    {
        // The cancel action is runtime interruption only; the AgentJob
        // lifecycle is the sole terminal authority and must not be
        // re-touched (issue-479 D6).
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "cancelled" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var path = handler.Requests.Single().RequestUri?.PathAndQuery ?? string.Empty;
        Assert.DoesNotContain("/agent-jobs/", path, StringComparison.Ordinal);
        Assert.DoesNotContain("/jobs/", path, StringComparison.Ordinal);
        Assert.DoesNotContain("/dispatch", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_NotCancellable_SurfacesStateHonestly()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "not-cancellable" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("state: not-cancellable", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_TerminalState_SurfacesTerminal()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "completed" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("state: completed", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_SelectedJson_ProjectsState()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "cancelled" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", StableSessionId, "--json", "state"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"state\": \"cancelled\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_UnknownSession_SurfacesServerError()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Agent session nope not found", "session_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", "nope"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionCancel_RespectsProjectOption()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "cancelled" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", StableSessionId, "--project", "proj_other"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/proj_other/agent-sessions/{StableSessionId}/cancel", handler.Requests[0].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionCancel_ServerUnavailableSurfacesStandardError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new HttpRequestException("offline"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "cancel", StableSessionId], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, error.ToString(), StringComparison.Ordinal);
    }

    // ----- recovery (compact/reset) -----

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task SessionRecovery_Table_PrintsStableSessionId(string operation)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = StableSessionId,
                    status = "idle",
                    contextWindowSize = 8192,
                    contextWindowUsed = operation == "compact" ? 512 : 0,
                    contextUsagePercent = operation == "compact" ? 6.25 : 0.0,
                    contextWindowUsedBefore = 4096,
                    operation,
                    wasCompacted = operation == "compact",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", operation, StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/agent-sessions/{StableSessionId}/{operation}", request.RequestUri?.PathAndQuery);
        Assert.Equal("{}", request.Body);
        Assert.Contains($"session id: {StableSessionId}", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task SessionRecovery_NotFound_SurfacesServerError(string operation)
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session missing not found", null, HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", operation, "missing"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }
}
