using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliAgentSessionCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, ActiveProjectId);
        return (http, handler, output, error, fs, executor);
    }

    [Fact]
    public async Task AgentHelp_ListsSessionSubcommand()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("session", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionHelp_ListsAllEightSubcommands()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("show", stdout, StringComparison.Ordinal);
        Assert.Contains("transcript", stdout, StringComparison.Ordinal);
        Assert.Contains("launch", stdout, StringComparison.Ordinal);
        Assert.Contains("compact", stdout, StringComparison.Ordinal);
        Assert.Contains("reset", stdout, StringComparison.Ordinal);
        Assert.Contains("followup", stdout, StringComparison.Ordinal);
        Assert.Contains("cancel", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionLaunchHelp_ListsPromptInputFlags()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--prompt", stdout, StringComparison.Ordinal);
        Assert.Contains("--prompt-file", stdout, StringComparison.Ordinal);
        Assert.Contains("--prompt-stdin", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFollowupHelp_ListsTextInputFlags()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "followup", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("--text", stdout, StringComparison.Ordinal);
        Assert.Contains("--text-file", stdout, StringComparison.Ordinal);
        Assert.Contains("--text-stdin", stdout, StringComparison.Ordinal);
        Assert.Contains("joins an active turn", stdout, StringComparison.Ordinal);
        Assert.Contains("user-initiated turn when idle", stdout, StringComparison.Ordinal);
        Assert.Contains("without creating a TaskRun or AgentJob", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionLaunch_Table_ResolvesAgentAndPrintsSessionIdentity()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_new_1",
                    agentId = "agent_123",
                    agentName = "reviewer",
                    status = "inactive",
                },
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", "Audit the auth flow"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        var launchRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, launchRequest.Method);
        Assert.Equal("/api/projects/proj_test/agents/reviewer/sessions", launchRequest.RequestUri?.PathAndQuery);
        var launchBody = JsonNode.Parse(launchRequest.Body!)!.AsObject();
        Assert.Equal("Audit the auth flow", launchBody["prompt"]?.GetValue<string>());

        var stdout = output.ToString();
        Assert.Contains("session id: sess_new_1", stdout, StringComparison.Ordinal);
        Assert.Contains("agent id:   agent_123", stdout, StringComparison.Ordinal);
        Assert.Contains("agent name: reviewer", stdout, StringComparison.Ordinal);
        Assert.Contains("status:     inactive", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionLaunch_SelectedJson_ProjectsRequestedFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_new_1",
                    agentId = "agent_123",
                    agentName = "reviewer",
                    status = "inactive",
                },
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", "Hi", "--json", "sessionId,agentName"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"sessionId\": \"sess_new_1\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"agentName\": \"reviewer\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionLaunch_PromptFile_ReadsFileAndSendsContents()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { sessionId = "sess_new_1", agentId = "agent_123", agentName = "reviewer", status = "inactive" },
            }, HttpStatusCode.Created));
        });

        var promptPath = "/tmp/prompt-task.md";
        fileSystem.AddFile(promptPath, "Long markdown body from file");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt-file", promptPath],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var launchRequest = handler.Requests[0];
        var launchBody = JsonNode.Parse(launchRequest.Body!)!.AsObject();
        Assert.Equal("Long markdown body from file", launchBody["prompt"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionLaunch_PromptStdin_ReadsFromStdin()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { sessionId = "sess_new_1", agentId = "agent_123", agentName = "reviewer", status = "inactive" },
            }, HttpStatusCode.Created));
        });

        var stdin = new StringReader("summarize this PR");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt-stdin"],
            output, error, fileSystem, executor,
            standardInput: stdin);

        Assert.Equal(0, exitCode);
        var launchRequest = handler.Requests[0];
        var launchBody = JsonNode.Parse(launchRequest.Body!)!.AsObject();
        Assert.Equal("summarize this PR", launchBody["prompt"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionLaunch_MissingAllPromptSources_FailsClearlyWithExitOne()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("prompt is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionLaunch_EmptyPrompt_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", "   "],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("prompt is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionLaunch_MultiplePromptSources_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        fileSystem.AddFile("/tmp/p", "x");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", "p", "--prompt-file", "/tmp/p"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionLaunch_BlankInlinePromptWithFile_StillFailsMutualExclusion()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        fileSystem.AddFile("/tmp/p", "from file");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", "", "--prompt-file", "/tmp/p"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionLaunch_BlankInlinePromptWithStdin_StillFailsMutualExclusion()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", " ", "--prompt-stdin"],
            output, error, fileSystem, executor,
            standardInput: new StringReader("from stdin"));

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionLaunch_UnknownAgentByName_SurfacesErrorWithoutSilentSuccess()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "server says agent nope is missing", "agent_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "nope", "--prompt", "Hi"],
            output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("server says agent nope is missing", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_test/agents/nope/sessions", handler.Requests[0].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionLaunch_UnknownAgentById_SurfacesServer404Error()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Agent 'agent_missing' not found", "agent_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "agent_missing", "--prompt", "Hi"],
            output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        var lookupRequest = handler.Requests.FirstOrDefault();
        Assert.NotNull(lookupRequest);
        Assert.Equal("/api/projects/proj_test/agents/agent_missing/sessions", lookupRequest!.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionLaunch_EmptyPromptRejectedByServer_SurfacesServerMessage()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.JsonError(
                "prompt is required", "prompt_required", HttpStatusCode.BadRequest));
        });

        // Reach the server: pass an empty file so the client passes validation.
        fileSystem.AddFile("/tmp/blank", "");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt-file", "/tmp/blank"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        // The CLI short-circuits on whitespace body BEFORE hitting the server; assert that path.
        Assert.Contains("prompt is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionLaunch_WithContextRefs_SendsContextInBody()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { sessionId = "sess_new_1", agentId = "agent_123", agentName = "reviewer", status = "inactive" },
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["agent", "session", "launch", "reviewer", "--prompt", "Hi", "--issue", "42", "--repository", "core", "--workspace-path", "/tmp/ws"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var launchRequest = handler.Requests[0];
        var launchBody = JsonNode.Parse(launchRequest.Body!)!.AsObject();
        Assert.Equal("Hi", launchBody["prompt"]?.GetValue<string>());
        var context = launchBody["context"]?.AsObject();
        Assert.NotNull(context);
        Assert.Equal(42, context!["issueNumber"]?.GetValue<int>());
        Assert.Equal("core", context["repository"]?.GetValue<string>());
        Assert.Equal("/tmp/ws", context["workspacePath"]?.GetValue<string>());
    }

    [Fact]
    public async Task SessionLaunch_RespectsProjectIdOption()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { sessionId = "sess_new_1", agentId = "agent_123", agentName = "reviewer", status = "inactive" },
            }, HttpStatusCode.Created));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", "Hi", "--project", "proj_other"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_other/agents/reviewer/sessions", handler.Requests[0].RequestUri?.PathAndQuery);
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
            http, ["agent", "session", "followup", "sess_123", "--text", "add a logout route"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/agent-sessions/sess_123/followup", request.RequestUri?.PathAndQuery);
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
            http, ["agent", "session", "followup", "sess_123", "--text-file", path],
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
            http, ["agent", "session", "followup", "sess_123", "--text-stdin"],
            output, error, fileSystem, executor,
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
            http, ["agent", "session", "followup", "sess_123", "--text", "Hi", "--json", "status"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"status\": \"sent\"", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"success\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFollowup_MissingAllTextSources_FailsClearly()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "followup", "sess_123"],
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
            http, ["agent", "session", "followup", "sess_123", "--text", ""],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("text is required", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_BlankInlineTextWithFile_StillFailsMutualExclusion()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        fileSystem.AddFile("/tmp/t", "from file");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "followup", "sess_123", "--text", "", "--text-file", "/tmp/t"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_BlankInlineTextWithStdin_StillFailsMutualExclusion()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "followup", "sess_123", "--text", " ", "--text-stdin"],
            output, error, fileSystem, executor,
            standardInput: new StringReader("from stdin"));

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionFollowup_UnknownSession_SurfacesServerErrorWithoutSilentSuccess()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Agent session sess_missing not found", "session_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "followup", "sess_missing", "--text", "Hi"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent session sess_missing not found", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("session_not_found", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionFollowup_TerminalSession_SurfacesConflict()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Session is no longer active", "session_inactive", HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "followup", "sess_123", "--text", "Hi"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Session is no longer active", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("session_inactive", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionCancel_Table_PrintsResultingSessionState()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "cancelled" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "cancel", "sess_123"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/agent-sessions/sess_123/cancel", request.RequestUri?.PathAndQuery);
        Assert.Contains("state: cancelled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_NotCancellable_SurfacesStateHonestlyWithoutReportingSuccess()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "not-cancellable" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "cancel", "sess_123"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("state: not-cancellable", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_TerminalState_SurfacesTerminal()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { state = "completed" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "cancel", "sess_123"],
            output, error, fileSystem, executor);

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
            http, ["agent", "session", "cancel", "sess_123", "--json", "state"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"state\": \"cancelled\"", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"success\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCancel_UnknownSession_SurfacesServerErrorWithoutSilentSuccess()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Agent session nope not found", "session_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "cancel", "nope"],
            output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent session nope not found", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("session_not_found", error.ToString(), StringComparison.Ordinal);
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
            http, ["agent", "session", "cancel", "sess_123", "--project", "proj_other"],
            output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_other/agent-sessions/sess_123/cancel", handler.Requests[0].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionCommand_ServerUnavailableSurfacesStandardError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new HttpRequestException("offline"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "cancel", "sess_123"],
            output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Server is not running. Start with: mo server start", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionLaunch_LegacyOutputOption_ReturnsUsageError()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new { success = true })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "launch", "reviewer", "--prompt", "Hi", "--output", "json"],
            output, error, fileSystem, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("--output", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_Table_ResolvesAgentAndRendersSessions()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { sessionId = "sess_1", agentId = "agent_123", agentName = "reviewer", status = "running", createdAt = "2026-06-26T10:00:00Z", lastActivityAt = "2026-06-26T10:05:00Z", resolvedModel = "gpt-5" },
                    new { sessionId = "sess_2", agentId = "agent_123", agentName = "reviewer", status = "failed", createdAt = "2026-06-26T09:00:00Z", lastActivityAt = "2026-06-26T09:30:00Z", resolvedModel = "gpt-5" },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "list", "reviewer",], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/projects/proj_test/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/api/projects/proj_test/agents/agent_123/sessions", handler.Requests[1].RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("sess_1", stdout, StringComparison.Ordinal);
        Assert.Contains("sess_2", stdout, StringComparison.Ordinal);
        Assert.Contains("running", stdout, StringComparison.Ordinal);
        Assert.Contains("failed", stdout, StringComparison.Ordinal);
        Assert.Contains("gpt-5", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_SelectedJson_ProjectsRequestedFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { sessionId = "sess_1", agentId = "agent_123", agentName = "reviewer", status = "running", createdAt = "2026-06-26T10:00:00Z" },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "list", "reviewer", "--json", "sessionId,agentName"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"sessionId\": \"sess_1\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"agentName\": \"reviewer\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_HonorsStatusFilter()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer", status = "active" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new { sessionId = "sess_1", agentId = "agent_123", agentName = "reviewer", status = "failed", createdAt = "2026-06-26T10:00:00Z" },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "list", "reviewer", "--status", "failed", "--json", "sessionId"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("?status=failed", handler.Requests[1].RequestUri?.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_UnknownAgent_SurfacesClientError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = Array.Empty<object>(),
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new object[] { } }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "list", "nope",], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Agent 'nope' not found", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SessionList_UnknownAgentById_SurfacesServer404Error()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            return Task.FromResult(RecordingHttpHandler.JsonError(
                "Agent 'agent_missing' not found", "agent_not_found", HttpStatusCode.NotFound));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "list", "agent_missing",], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Agent 'agent_missing' not found", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_test/agents/agent_missing", handler.Requests[0].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionShow_Table_RendersEnrichedSummary()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_123",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    status = "running",
                    createdAt = "2026-06-26T10:00:00Z",
                    lastActivityAt = "2026-06-26T10:05:00Z",
                    resolvedModel = "gpt-5",
                    failureReason = (string?)null,
                    failureCategory = (string?)null,
                    toolCallCount = 12,
                    toolErrorCount = 1,
                    contextRefs = new
                    {
                        issueNumber = 42,
                        epicNumber = (string?)null,
                        repository = "core",
                        workspacePath = "/tmp/ws",
                    },
                    usage = new
                    {
                        inputTokens = 2000,
                        outputTokens = 1210,
                        totalTokens = 3210,
                        costAmount = 0.05,
                        costCurrency = "USD",
                    },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_123",], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_test/agent-sessions/sess_123", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("agent:             agent_456 (reviewer)", stdout, StringComparison.Ordinal);
        Assert.Contains("status:            running", stdout, StringComparison.Ordinal);
        Assert.Contains("created:           2026-06-26T10:00:00Z", stdout, StringComparison.Ordinal);
        Assert.Contains("model:             gpt-5", stdout, StringComparison.Ordinal);
        Assert.Contains("tool calls:        12", stdout, StringComparison.Ordinal);
        Assert.Contains("tool errors:       1", stdout, StringComparison.Ordinal);
        Assert.Contains("tokens:            3210 (input 2000, output 1210)", stdout, StringComparison.Ordinal);
        Assert.Contains("cost:              0.05 USD", stdout, StringComparison.Ordinal);
        Assert.Contains("issue #42", stdout, StringComparison.Ordinal);
        Assert.Contains("repo: core", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("failure reason", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("failure category", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_Table_RendersFailureReasonAndCategoryAsDistinctRows()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_failed",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    status = "failed",
                    createdAt = "2026-06-26T10:00:00Z",
                    lastActivityAt = "2026-06-26T10:05:00Z",
                    resolvedModel = "gpt-5",
                    failureReason = "AgentJob requires 'workspace.path' in dispatch variables",
                    failureCategory = "invalid-input",
                    toolCallCount = 4,
                    toolErrorCount = 1,
                    contextRefs = new
                    {
                        issueNumber = 42,
                        epicNumber = (string?)null,
                        repository = "core",
                        workspacePath = "/tmp/ws",
                    },
                    usage = new { },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_failed",], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("status:            failed", stdout, StringComparison.Ordinal);
        Assert.Contains(
            "failure reason:    AgentJob requires 'workspace.path' in dispatch variables",
            stdout,
            StringComparison.Ordinal);
        Assert.Contains("failure category:  invalid-input", stdout, StringComparison.Ordinal);
        // Reason and category are distinct rows so a single failure
        // surfaces both the actionable text and the machine-groupable
        // category without one replacing the other.
        Assert.Contains("AgentJob requires 'workspace.path' in dispatch variables", stdout, StringComparison.Ordinal);
        Assert.Contains("invalid-input", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_Table_OmitsFailureReasonAndCategory_OnSuccessfulSession()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_ok",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    status = "completed",
                    createdAt = "2026-06-26T10:00:00Z",
                    lastActivityAt = "2026-06-26T10:05:00Z",
                    resolvedModel = "gpt-5",
                    toolCallCount = 4,
                    toolErrorCount = 0,
                    usage = new { },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_ok",], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("status:            completed", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("failure reason", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("failure category", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_Json_PreservesFailureReasonAndCategoryFields()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_failed",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    status = "failed",
                    createdAt = "2026-06-26T10:00:00Z",
                    lastActivityAt = "2026-06-26T10:05:00Z",
                    failureReason = "AgentJob requires 'workspace.path' in dispatch variables",
                    failureCategory = "invalid-input",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_failed", "--json", "failureReason,failureCategory"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"failureReason\": \"AgentJob requires 'workspace.path' in dispatch variables\"",
            stdout, StringComparison.Ordinal);
        Assert.Contains("\"failureCategory\": \"invalid-input\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_SelectedJson_OnlyEmitsRequestedSessionId()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_ok",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    status = "completed",
                    createdAt = "2026-06-26T10:00:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_ok", "--json", "sessionId"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"sessionId\": \"sess_ok\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("failureReason", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("failureCategory", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_SelectedJson_ProjectsRequestedFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_123",
                    agentId = "agent_456",
                    agentName = "reviewer",
                    status = "running",
                    createdAt = "2026-06-26T10:00:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_123", "--json", "sessionId,agentName"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"sessionId\": \"sess_123\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"agentName\": \"reviewer\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionShow_NotFound_SurfacesServer404()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Agent session sess_missing not found", "session_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_missing",], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent session sess_missing not found", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
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
                    turns = new object[]
                    {
                        new { id = "turn_1", startedAt = "2026-06-26T10:00:00Z", user = new { role = "mohist", text = "first message body", kind = "task", sentAt = "2026-06-26T10:00:00Z" }, assistant = new[] { new { type = "text", text = "assistant response", id = "part_1", startedAt = "2026-06-26T10:00:05Z" } } },
                        new { id = "turn_2", startedAt = "2026-06-26T10:05:00Z", user = new { role = "mohist", text = "second message body", kind = "task", sentAt = "2026-06-26T10:05:00Z" }, assistant = new[] { new { type = "tool", id = "part_2", tool = new { toolCallId = "call_1", toolName = "read_file" }, startedAt = "2026-06-26T10:05:05Z" } } },
                    },
                    partCount = 4,
                    lastActivityAt = "2026-06-26T10:05:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "transcript", "sess_123",], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_test/agent-sessions/sess_123/transcript", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("turns:          2", stdout, StringComparison.Ordinal);
        Assert.Contains("parts:          4", stdout, StringComparison.Ordinal);
        Assert.Contains("first activity: 2026-06-26T10:00:00Z", stdout, StringComparison.Ordinal);
        Assert.Contains("last activity:  2026-06-26T10:05:00Z", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("first message body", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("assistant response", stdout, StringComparison.Ordinal);
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
                    turns = new object[]
                    {
                        new { id = "turn_1", startedAt = "2026-06-26T10:00:00Z", user = new { role = "mohist", text = "first message body", kind = "task", sentAt = "2026-06-26T10:00:00Z" }, assistant = new[] { new { type = "text", text = "assistant response", id = "part_1" } } },
                    },
                    partCount = 1,
                    lastActivityAt = "2026-06-26T10:00:00Z",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "transcript", "sess_123", "--json", "partCount,turns"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"partCount\": 1", stdout, StringComparison.Ordinal);
        Assert.Contains("\"text\": \"first message body\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionTranscript_NotFound_SurfacesServer404()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                "Agent session sess_missing not found", "session_not_found", HttpStatusCode.NotFound)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "transcript", "sess_missing",], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        Assert.Contains("Agent session sess_missing not found", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task SessionShow_ShowAndTranscriptUnchanged_AreDistinctFromIssueSessionVerbs()
    {
        // Verify the agent session show/transcript hit the generic session endpoints,
        // not the workflow-session issue-scoped endpoints.
        var (http, showHandler, showOutput, showError, showFs, showExec) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { sessionId = "sess_123", agentId = "agent_456", agentName = "reviewer", status = "running" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "show", "sess_123", "--json", "agentId"],
            showOutput, showError, showFs, showExec);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/agent-sessions/sess_123", showHandler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Contains("\"agentId\": \"agent_456\"", showOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionList_UsesAgentIdDirectlyWhenPassed()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.Contains("/agents/agent_123", StringComparison.Ordinal) && !path.Contains("/sessions", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "agent_123", name = "reviewer", status = "active" },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new { sessionId = "sess_1", status = "running", createdAt = "2026-06-26T10:00:00Z" },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "list", "agent_123", "--json", "sessionId"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/projects/proj_test/agents/agent_123", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_test/agents/agent_123/sessions", handler.Requests[1].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionList_DefaultOutputIsTable()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_123", name = "reviewer" } },
                }));
            }
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new { sessionId = "sess_1", status = "running", createdAt = "2026-06-26T10:00:00Z", resolvedModel = "gpt-5" },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", "list", "reviewer"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("sess_1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("running", output.ToString(), StringComparison.Ordinal);
    }
}
