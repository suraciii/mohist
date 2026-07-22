using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliAgentSessionRecoverySpecs
{
    private const string ActiveProjectId = "proj_test";
    private const string StableSessionId = "sess_stable";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, ActiveProjectId);
        return (http, handler, output, error, fs, executor);
    }

    [Theory]
    [InlineData("compact", "Compact the session in place")]
    [InlineData("reset", "Reset the session in place")]
    public async Task RecoveryHelp_DescribesInPlaceOperation(string operation, string description)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", operation, "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains(description, stdout, StringComparison.Ordinal);
        Assert.Contains("Stable AgentSession id", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("new session id", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rotat", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task Recovery_Table_PostsToCanonicalSessionAndPrintsStableSessionId(string operation)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecoveryResponse(operation)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", operation, StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/agent-sessions/{StableSessionId}/{operation}", request.RequestUri?.PathAndQuery);
        Assert.Equal("{}", request.Body);
        var stdout = output.ToString();
        Assert.Contains($"session id: {StableSessionId}", stdout, StringComparison.Ordinal);
        Assert.Contains($"operation:   {operation}", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("New session", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task Recovery_Json_PrintsStableSessionIdWithoutRotatedId(string operation)
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecoveryResponse(operation)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", operation, StableSessionId, "--json", "id"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains($"\"id\": \"{StableSessionId}\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("agentSessionId", stdout, StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task Recovery_SessionActive_ReferencesStableSessionId(string operation)
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                $"AgentSession {StableSessionId} is currently active; Compact and Reset require an idle session.",
                "session_active",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", operation, StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        AssertStableIdentityError(error.ToString(), "session_active");
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task Recovery_RuntimeSessionMissing_ReferencesStableSessionId(string operation)
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.JsonError(
                $"Runtime session missing for AgentSession {StableSessionId}: no runtime session is bound. Reset the session to establish a new binding.",
                "runtime_session_missing",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", operation, StableSessionId], output, error, fileSystem, executor);

        Assert.Equal(1, exitCode);
        AssertStableIdentityError(error.ToString(), "runtime_session_missing");
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task Recovery_ProjectOverride_UsesSelectedProject(string operation)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecoveryResponse(operation)));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", operation, StableSessionId, "--project", "proj_other"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/proj_other/agent-sessions/{StableSessionId}/{operation}", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task Recovery_LegacyOutputOption_FailsWithoutCallingApi(string operation)
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called when output mode is invalid"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "session", operation, StableSessionId, "--output", "json"], output, error, fileSystem, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("--output", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage RecoveryResponse(string operation) =>
        RecordingHttpHandler.Json(new
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
        });

    private static void AssertStableIdentityError(string stderr, string code)
    {
        Assert.Contains($"AgentSession {StableSessionId}", stderr, StringComparison.Ordinal);
        Assert.Contains(code, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("new session id", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rotat", stderr, StringComparison.OrdinalIgnoreCase);
    }
}
