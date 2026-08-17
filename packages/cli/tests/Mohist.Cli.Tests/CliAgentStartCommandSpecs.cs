using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public partial class CliAgentCommandSpecs
{
    [Fact]
    public async Task AgentStartHelp_ListsTaskFirstFlagsWithoutAnAgentArgument()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true })));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "start", "--help"], output);

        Assert.Equal(0, exitCode);
        var help = output.ToString();
        Assert.Contains("--prompt", help, StringComparison.Ordinal);
        Assert.Contains("--prompt-file", help, StringComparison.Ordinal);
        Assert.Contains("--attach", help, StringComparison.Ordinal);
        Assert.Contains("--name", help, StringComparison.Ordinal);
        Assert.Contains("--runtime", help, StringComparison.Ordinal);
        Assert.Contains("--model", help, StringComparison.Ordinal);
        Assert.Contains("--variant", help, StringComparison.Ordinal);
        Assert.Contains("--issue", help, StringComparison.Ordinal);
        Assert.Contains("--epic", help, StringComparison.Ordinal);
        Assert.Contains("--repo", help, StringComparison.Ordinal);
        Assert.Contains("--workspace", help, StringComparison.Ordinal);
        Assert.Contains("--project", help, StringComparison.Ordinal);
        Assert.Contains("--idempotency-key", help, StringComparison.Ordinal);
        Assert.DoesNotContain("<agent>", help, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentStart_SendsTaskHintsContextAndCliOrigin()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                jobId = "job-start",
                sessionId = "session-start",
                inputId = "input-start",
                turnId = "turn-start",
                agentId = "agent-start",
                agentName = "task-agent",
                workspaceId = "task-workspace",
                targetId = "agent-start",
                origin = "cli",
                status = "queued",
                sessionUrl = "/project/sessions/session-start",
                transcriptUrl = "/api/projects/proj_123/agent-sessions/session-start/transcript",
                jobUrl = "/api/projects/proj_123/agent-jobs/job-start",
                observationUrl = "/api/projects/proj_123/agent-jobs/job-start/launch-observation",
                scopeFingerprint = "scope-start",
                execution = new { runtime = "pi", model = "provider/model", variant = "balanced" },
                repository = "server",
                workspace = "review",
                workspaceRepositories = new[] { "server" },
                issueNumber = 42,
                epicNumber = 7,
                permissionScope = "project-workspace-write",
                expectedImpact = "Starts one AgentJob and AgentSession",
            },
        }, HttpStatusCode.Created)));
        var output = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            [
                "agent", "start", "--prompt", "Inspect the task", "--name", "task-agent",
                "--runtime", "pi", "--model", "provider/model", "--variant", "balanced",
                "--issue", "42", "--epic", "7", "--repo", "server", "--workspace", "review",
                "--project", "proj_123", "--idempotency-key", "start-key", "--yes",
            ],
            output: output,
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_123/agent-tasks", request.RequestUri?.PathAndQuery);
        Assert.Equal("scope-start", request.Headers["X-Mohist-Agent-Preflight"].Single());
        Assert.Equal("start-key", request.Headers["Idempotency-Key"].Single());
        Assert.Equal("cli", request.Headers["X-Mohist-Launch-Origin"].Single());
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("Inspect the task", body["prompt"]?.GetValue<string>());
        Assert.Equal("task-agent", body["name"]?.GetValue<string>());
        Assert.Equal("pi", body["runtime"]?.GetValue<string>());
        Assert.Equal("provider/model", body["model"]?.GetValue<string>());
        Assert.Equal("balanced", body["variant"]?.GetValue<string>());
        Assert.Equal(42, body["context"]?["issueNumber"]?.GetValue<int>());
        Assert.Equal(7, body["context"]?["epicNumber"]?.GetValue<int>());
        Assert.Equal("server", body["context"]?["repository"]?.GetValue<string>());
        Assert.Equal("review", body["context"]?["workspace"]?.GetValue<string>());

        var text = output.ToString();
        Assert.Contains("agent id:   agent-start", text, StringComparison.Ordinal);
        Assert.Contains("agent name: task-agent", text, StringComparison.Ordinal);
        Assert.Contains("job id:     job-start", text, StringComparison.Ordinal);
        Assert.Contains("session id: session-start", text, StringComparison.Ordinal);
        Assert.Contains("input id:   input-start", text, StringComparison.Ordinal);
        Assert.Contains("turn id:    turn-start", text, StringComparison.Ordinal);
        Assert.Contains("workspace:  task-workspace", text, StringComparison.Ordinal);
        Assert.Contains("status:     queued", text, StringComparison.Ordinal);
        Assert.Contains("session:    /project/sessions/session-start", text, StringComparison.Ordinal);
        Assert.Contains("transcript: /api/projects/proj_123/agent-sessions/session-start/transcript", text, StringComparison.Ordinal);
        Assert.Contains("job:        /api/projects/proj_123/agent-jobs/job-start", text, StringComparison.Ordinal);
        Assert.Contains("observation: /api/projects/proj_123/agent-jobs/job-start/launch-observation", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStart_AttachmentOnlyTaskSendsAnEmptyPromptAndAttachmentId()
    {
        var handler = new RecordingHttpHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/attachments", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "att-start", fileName = "notes.md", contentType = "text/markdown", size = 5 },
                }));
            }

            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { jobId = "job-start" },
            }, HttpStatusCode.Created));
        });
        var fileSystem = FileSystemWithProject();
        fileSystem.AddFile("/tmp/notes.md", "hello");

        var exitCode = await RunAsync(
            handler,
            ["agent", "start", "--attach", "/tmp/notes.md", "--idempotency-key", "attachment-key"],
            fileSystem: fileSystem);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("", body["prompt"]?.GetValue<string>());
        Assert.Equal("att-start", body["attachments"]?[0]?.GetValue<string>());
    }

    [Fact]
    public async Task AgentStart_GeneratedKeyIsPrintedBeforeTaskRequest()
    {
        var output = new StringWriter();
        var observedOutput = string.Empty;
        var handler = new RecordingHttpHandler((_, _) =>
        {
            observedOutput = output.ToString();
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { jobId = "job-start" },
            }, HttpStatusCode.Created));
        });

        var exitCode = await RunAsync(
            handler,
            ["agent", "start", "--prompt", "Inspect"],
            output: output,
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Contains("Idempotency-Key:", observedOutput, StringComparison.Ordinal);
        Assert.NotEmpty(handler.Requests.Single().Headers["Idempotency-Key"].Single());
    }

    [Fact]
    public async Task AgentStart_RawJsonPrintsServerEnvelopeAndDoesNotPrintGeneratedKey()
    {
        const string rawResponse = "{\"success\":true,\"data\":{\"agentId\":\"agent-start\",\"jobId\":\"job-start\"}}";
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(rawResponse),
        }));
        var output = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "start", "--prompt", "Inspect", "--json"],
            output: output,
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Equal(rawResponse, output.ToString());
        Assert.DoesNotContain("Idempotency-Key:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStart_RawJsonPreservesRejectedServerEnvelopeAndExitsNonZero()
    {
        const string rawResponse = "{\"success\":false,\"error\":\"Execution configuration is unresolved.\",\"code\":\"execution_config_unresolvable\"}";
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(rawResponse),
        }));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "start", "--prompt", "Inspect", "--json"],
            output: output,
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        Assert.Equal(rawResponse, output.ToString());
        Assert.Contains("mo agent model list", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--prompt", "Inspect", "--prompt-file", "task.md")]
    [InlineData("--runtime", "unknown")]
    [InlineData("--model", "not-a-provider-model")]
    public async Task AgentStart_InvalidFlagsFailBeforeAnyRequest(params string[] invalidFlags)
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();
        var args = new List<string> { "agent", "start", "--prompt", "Inspect" };
        args.AddRange(invalidFlags);

        var exitCode = await RunAsync(
            handler,
            args.ToArray(),
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("USAGE", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentStart_MissingExecutionConfigPrintsRepairsAndModelCatalogEntry()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = false,
            error = "Execution configuration is unresolved.",
            code = "execution_config_unresolvable",
            details = new { repairs = new[] { "supply runtime/model/variant hints", "configure the Project default execution configuration" } },
        }, HttpStatusCode.Conflict)));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "start", "--prompt", "Inspect"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        var errorText = error.ToString();
        Assert.Contains("execution_config_unresolvable", errorText, StringComparison.Ordinal);
        Assert.Contains("--runtime/--model/--variant", errorText, StringComparison.Ordinal);
        Assert.Contains("configure the Project default", errorText, StringComparison.Ordinal);
        Assert.Contains("mo agent model list", errorText, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AgentStart_PendingConvergenceInstructsRetryWithSameKey()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = false,
            error = "Agent launch setup is still recovering.",
            code = "launch_setup_pending",
        }, HttpStatusCode.ServiceUnavailable)));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "start", "--prompt", "Inspect", "--idempotency-key", "pending-key"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.NotEqual(0, exitCode);
        Assert.Contains("launch_setup_pending", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("same --idempotency-key pending-key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStart_RetryWithSameKeyReturnsTheSameProjection()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new
            {
                agentId = "agent-start",
                agentName = "Inspect",
                jobId = "job-start",
                sessionId = "session-start",
                inputId = "input-start",
                turnId = "turn-start",
                status = "queued",
            },
        }, HttpStatusCode.Created)));
        var firstOutput = new StringWriter();
        var secondOutput = new StringWriter();
        string[] args = ["agent", "start", "--prompt", "Inspect", "--idempotency-key", "same-key"];

        Assert.Equal(0, await RunAsync(handler, args, output: firstOutput, fileSystem: FileSystemWithProject()));
        Assert.Equal(0, await RunAsync(handler, args, output: secondOutput, fileSystem: FileSystemWithProject()));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("same-key", request.Headers["Idempotency-Key"].Single()));
        Assert.Contains("agent-start", firstOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent-start", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("session-start", secondOutput.ToString(), StringComparison.Ordinal);
    }
}
