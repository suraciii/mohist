using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueSessionSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = ActiveProjectId)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        return (http, handler, output, error, fs, executor);
    }

    [Fact]
    public async Task SessionHelp_ListsFiveSubcommandsAndDocumentsNameSource()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("show", stdout, StringComparison.Ordinal);
        Assert.Contains("transcript", stdout, StringComparison.Ordinal);
        Assert.Contains("compact", stdout, StringComparison.Ordinal);
        Assert.Contains("reset", stdout, StringComparison.Ordinal);
        Assert.Contains("followup", stdout, StringComparison.Ordinal);
        Assert.Contains("mo issue sessions", stdout, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("compact", "Compact the session in place")]
    [InlineData("reset", "Reset the session in place")]
    public async Task SessionRecoveryHelp_DescribesInPlaceOperation(string operation, string description)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", operation, "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains(description, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("new session id", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rotat", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionsList_Unchanged_HitsCoderSessionsEndpoint()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { id = "sess_1", sessionName = "plan", status = "idle", createdAt = "2026-06-26T10:00:00Z", model = "gpt-5" },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "sessions", "42", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/coder-sessions", request.RequestUri?.PathAndQuery);
        Assert.Contains("plan", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_Table_RendersMetadata()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_1",
                    sessionName = "plan",
                    status = "idle",
                    model = "gpt-5",
                    stage = "plan",
                    createdAt = "2026-06-26T10:00:00Z",
                    usage = new
                    {
                        inputTokens = 2000,
                        outputTokens = 1210,
                        totalTokens = 3210,
                        contextWindowUsed = 1234,
                        contextWindowSize = 8192,
                        contextUsagePercent = 15.06,
                        healthStatus = "healthy",
                    },
                    metadata = new { partCount = 12, toolCount = 3 },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "show", "42", "plan", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("name:      plan", stdout, StringComparison.Ordinal);
        Assert.Contains("status:    idle", stdout, StringComparison.Ordinal);
        Assert.Contains("model:     gpt-5", stdout, StringComparison.Ordinal);
        Assert.Contains("tokens:    3210 (input 2000, output 1210)", stdout, StringComparison.Ordinal);
        Assert.Contains("context:   1234/8192 (15.06)", stdout, StringComparison.Ordinal);
        Assert.Contains("health:    healthy", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_Json_EmitsRawPayload()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "sess_1", sessionName = "plan", status = "idle" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "show", "42", "plan", "-o", "json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"sessionName\": \"plan\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"idle\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_NotFound_SurfacesError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session missing not found",
                null,
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "show", "42", "missing", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Session missing not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionTranscript_Table_RendersSummaryNotFullDump()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    turns = new[]
                    {
                        new { id = "turn_1", startedAt = "2026-06-26T10:00:00Z", assistant = new[] { new { type = "text", text = "first message body" } } },
                        new { id = "turn_2", startedAt = "2026-06-26T10:05:00Z", assistant = new[] { new { type = "text", text = "second message body" } } },
                    },
                    partCount = 4,
                    lastActivityAt = "2026-06-26T10:05:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "transcript", "42", "plan", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan/transcript", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("turns:          2", stdout, StringComparison.Ordinal);
        Assert.Contains("parts:          4", stdout, StringComparison.Ordinal);
        Assert.Contains("first activity: 2026-06-26T10:00:00Z", stdout, StringComparison.Ordinal);
        Assert.Contains("last activity:  2026-06-26T10:05:00Z", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("first message body", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("second message body", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionTranscript_DefaultOutput_RendersSummaryNotFullDump()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    turns = new[]
                    {
                        new { id = "turn_1", startedAt = "2026-06-26T10:00:00Z", assistant = new[] { new { type = "text", text = "full transcript body" } } },
                    },
                    partCount = 2,
                    lastActivityAt = "2026-06-26T10:02:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "transcript", "42", "plan"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan/transcript", handler.Requests.Single().RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("turns:          1", stdout, StringComparison.Ordinal);
        Assert.Contains("parts:          2", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("full transcript body", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionTranscript_Json_EmitsFullTranscript()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
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
            http, ["issue", "session", "transcript", "42", "plan", "-o", "json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"text\": \"first message body\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"partCount\": 1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionTranscript_NotFound_SurfacesError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session missing not found",
                null,
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "transcript", "42", "missing", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Session missing not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCompact_Table_PrintsStableSessionId()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_1",
                    status = "idle",
                    contextWindowSize = 8192,
                    contextWindowUsed = 512,
                    contextUsagePercent = 6.25,
                    contextWindowUsedBefore = 4096,
                    operation = "compact",
                    wasCompacted = true,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "compact", "42", "plan", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan/compact", request.RequestUri?.PathAndQuery);
        Assert.Equal("{}", request.Body);
        var stdout = output.ToString();
        Assert.Contains("session id: sess_1", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("New session", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("context:     4096 → 512 (6.25)", stdout, StringComparison.Ordinal);
        Assert.Contains("operation:   compact", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCompact_DefaultOutput_PrintsStableSessionId()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_1",
                    status = "idle",
                    contextWindowSize = 8192,
                    contextWindowUsed = 512,
                    contextUsagePercent = 6.25,
                    contextWindowUsedBefore = 4096,
                    operation = "compact",
                    wasCompacted = true,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "compact", "42", "plan"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan/compact", request.RequestUri?.PathAndQuery);
        Assert.Contains("session id: sess_1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCompact_Json_EmitsRawRecoveryPayload()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_1",
                    status = "idle",
                    operation = "compact",
                    wasCompacted = true,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "compact", "42", "plan", "-o", "json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"id\": \"sess_1\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("agentSessionId", stdout, StringComparison.Ordinal);
        Assert.Contains("\"wasCompacted\": true", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCompact_NotFound_SurfacesError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session missing not found",
                null,
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "compact", "42", "missing", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Session missing not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCompact_Active_SurfacesSessionActiveConflict()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "AgentSession sess_1 is currently active; Compact and Reset require an idle session.",
                "session_active",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "compact", "42", "plan", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        var stderr = error.ToString();
        Assert.Contains("AgentSession sess_1 is currently active", stderr, StringComparison.Ordinal);
        Assert.Contains("session_active", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("new session id", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rotat", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionReset_Table_PrintsStableSessionId()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_1",
                    status = "idle",
                    contextWindowSize = 8192,
                    contextWindowUsed = 0,
                    contextUsagePercent = 0.0,
                    contextWindowUsedBefore = 4096,
                    operation = "reset",
                    wasCompacted = false,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "reset", "42", "plan", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan/reset", request.RequestUri?.PathAndQuery);
        Assert.Equal("{}", request.Body);
        Assert.Contains("session id: sess_1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReset_DefaultOutput_PrintsStableSessionId()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_1",
                    status = "idle",
                    contextWindowSize = 8192,
                    contextWindowUsed = 0,
                    contextUsagePercent = 0.0,
                    contextWindowUsedBefore = 4096,
                    operation = "reset",
                    wasCompacted = false,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "reset", "42", "plan"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan/reset", request.RequestUri?.PathAndQuery);
        Assert.Contains("session id: sess_1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReset_Json_EmitsRawRecoveryPayload()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    id = "sess_1",
                    status = "idle",
                    operation = "reset",
                    wasCompacted = false,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "reset", "42", "plan", "-o", "json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"id\": \"sess_1\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("agentSessionId", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReset_NotFound_SurfacesError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session missing not found",
                null,
                HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "reset", "42", "missing", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Session missing not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReset_Active_SurfacesSessionActiveConflict()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "AgentSession sess_1 is currently active; Compact and Reset require an idle session.",
                "session_active",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "reset", "42", "plan", "-o", "table"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        var stderr = error.ToString();
        Assert.Contains("AgentSession sess_1 is currently active", stderr, StringComparison.Ordinal);
        Assert.Contains("session_active", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("new session id", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rotat", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task SessionRecovery_RuntimeSessionMissing_ReferencesStableSessionId(string operation)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Runtime session missing for AgentSession sess_1: no runtime session is bound. Reset the session to establish a new binding.",
                "runtime_session_missing",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", operation, "42", "plan"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        var stderr = error.ToString();
        Assert.Contains("AgentSession sess_1", stderr, StringComparison.Ordinal);
        Assert.Contains("runtime_session_missing", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("new session id", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rotat", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("show", new string[] { })]
    [InlineData("transcript", new string[] { })]
    [InlineData("compact", new string[] { })]
    [InlineData("reset", new string[] { })]
    [InlineData("followup", new string[] { "--text", "x" })]
    public async Task SessionSubcommand_ProjectIdOverride_UsesProjectIdArgument(string verb, string[] extraArgs)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((req, _) =>
        {
            var isPost = req.Method == HttpMethod.Post;
            object payload = isPost
                ? new { success = true, data = new { id = "sess_1", status = "idle", operation = verb, wasCompacted = true } }
                : new { success = true, data = new { id = "sess_1", sessionName = "plan", status = "idle" } };
            return Task.FromResult(RecordingHttpHandler.Json(payload));
        });

        var args = new List<string> { "issue", "session", verb, "42", "plan", "--project-id", "proj_by_id", "-o", "json" };
        args.AddRange(extraArgs);
        var exitCode = await MohistCliCommands.RunAsync(
            http, [.. args], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/proj_by_id/issues/42/sessions/plan{(verb == "show" ? "" : "/" + verb)}", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Theory]
    [InlineData("show", new string[] { })]
    [InlineData("transcript", new string[] { })]
    [InlineData("compact", new string[] { })]
    [InlineData("reset", new string[] { })]
    [InlineData("followup", new string[] { "--text", "x" })]
    public async Task SessionSubcommand_ProjectOverride_UsesProjectArgument(string verb, string[] extraArgs)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((req, _) =>
        {
            var isPost = req.Method == HttpMethod.Post;
            object payload = isPost
                ? new { success = true, data = new { id = "sess_1", status = "idle", operation = verb, wasCompacted = true } }
                : new { success = true, data = new { id = "sess_1", sessionName = "plan", status = "idle" } };
            return Task.FromResult(RecordingHttpHandler.Json(payload));
        });

        var args = new List<string> { "issue", "session", verb, "42", "plan", "--project", "proj_override", "-o", "json" };
        args.AddRange(extraArgs);
        var exitCode = await MohistCliCommands.RunAsync(
            http, [.. args], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/proj_override/issues/42/sessions/plan{(verb == "show" ? "" : "/" + verb)}", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("transcript")]
    [InlineData("compact")]
    [InlineData("reset")]
    [InlineData("followup")]
    public async Task SessionSubcommand_InvalidOutput_FailsWithoutCallingApi(string verb)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when output mode is invalid"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", verb, "42", "plan", "-o", "yaml"], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("--output must be 'table' or 'json'", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_Table_PrintsDeliveryStatus()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "plan", "--text", "add a logout route"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/sessions/plan/followup", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("add a logout route", body["text"]?.GetValue<string>());
        Assert.Contains("delivery: sent", output.ToString(), StringComparison.Ordinal);
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
            http, ["issue", "session", "followup", "42", "plan", "--text-file", path],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!.AsObject();
        Assert.Equal("refactor this loop", body["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionFollowup_TextStdin_ReadsFromStdin()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));

        var stdin = new StringReader("please continue");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "plan", "--text-stdin"],
            output, error, fileSystem, executor,
            standardInput: stdin);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!.AsObject();
        Assert.Equal("please continue", body["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionFollowup_Json_PrintsRawPayload()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { status = "sent" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "plan", "--text", "Hi", "-o", "json"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"success\": true", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"sent\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFollowup_MissingAllTextSources_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "plan"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("text is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_EmptyText_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "plan", "--text", ""],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("text is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_SessionInactive_SurfacesConflict()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session is no longer active", "session_inactive", HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "plan", "--text", "Hi"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Session is no longer active", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("session_inactive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionFollowup_RunnerOffline_SurfacesError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Runner is offline", "runner_offline", HttpStatusCode.ServiceUnavailable)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "plan", "--text", "Hi"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Runner is offline", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("runner_offline", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionFollowup_UnknownSession_SurfacesServerError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session missing not found", "session_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "42", "missing", "--text", "Hi"],
            output, error, fileSystem, executor);

        Assert.Equal(4, exitCode);
        Assert.Contains("Session missing not found", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("session_not_found", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionFollowup_Help_ListsTextInputFlags()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "session", "followup", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--text", stdout, StringComparison.Ordinal);
        Assert.Contains("--text-file", stdout, StringComparison.Ordinal);
        Assert.Contains("--text-stdin", stdout, StringComparison.Ordinal);
        Assert.Contains("joins an active turn", stdout, StringComparison.Ordinal);
        Assert.Contains("user-initiated turn when idle", stdout, StringComparison.Ordinal);
        Assert.Contains("without creating a TaskRun or AgentJob", stdout, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }
}
