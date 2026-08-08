using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliWorkspaceCommandSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupEnv(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? activeProjectId = ActiveProjectId)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        return (http, handler, output, error, fs, executor);
    }

    private static (HttpClient http, RecordingHttpHandler handler, StringWriter output, StringWriter error, FakeFileSystem fileSystem, FakeCommandExecutor executor) SetupSync(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        string? activeProjectId = ActiveProjectId)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(responder, activeProjectId);
        return (http, handler, output, error, fs, executor);
    }

    // ----- workspace help -----

    [Fact]
    public async Task WorkspaceHelp_ListsSubcommands()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("USAGE", stdout, StringComparison.Ordinal);
        Assert.Contains("list", stdout, StringComparison.Ordinal);
        Assert.Contains("view", stdout, StringComparison.Ordinal);
        Assert.Contains("create", stdout, StringComparison.Ordinal);
        Assert.Contains("close", stdout, StringComparison.Ordinal);
        Assert.Contains("repo", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("show", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("delete", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceRepoHelp_ListsAddRemove()
    {
        var (http, _, output, error, fileSystem, executor) = SetupEnv((_, _) =>
            throw new InvalidOperationException("API must not be called for help"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "repo", "--help"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("add", stdout, StringComparison.Ordinal);
        Assert.Contains("remove", stdout, StringComparison.Ordinal);
    }

    // ----- list -----

    [Fact]
    public async Task WorkspaceList_SendsGetWithProjectPath()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(req =>
        {
            return RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { projectId = ActiveProjectId, name = "ws1", origin = new { kind = "manual" }, repositories = Array.Empty<string>(), status = "active", home = (object?)null, createdAt = "2025-01-01T00:00:00Z", archivedAt = (string?)null },
                },
            });
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_test/workspaces", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkspaceList_WithStatusFilter_AddsQueryParam()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list", "--status", "active"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/workspaces?status=active", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkspaceList_WithOriginFilter_AddsQueryParam()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list", "--origin", "manual"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/workspaces?origin=manual", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkspaceList_WithBothFilters_AddsBothQueryParams()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list", "--status", "active", "--origin", "manual"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/workspaces?status=active&origin=manual", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkspaceList_JsonFields_AreDiscovered()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list", "--json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("name", stdout, StringComparison.Ordinal);
        Assert.Contains("origin", stdout, StringComparison.Ordinal);
        Assert.Contains("repositories", stdout, StringComparison.Ordinal);
        Assert.Contains("status", stdout, StringComparison.Ordinal);
        Assert.Contains("home", stdout, StringComparison.Ordinal);
        Assert.Contains("createdAt", stdout, StringComparison.Ordinal);
        Assert.Contains("archivedAt", stdout, StringComparison.Ordinal);
        Assert.Contains("sessions", stdout, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ----- view -----

    [Fact]
    public async Task WorkspaceView_SendsGetWithNameInPath()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "view", "my-ws"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_test/workspaces/my-ws", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task WorkspaceView_JsonFields_AreDiscovered()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "view", "my-ws", "--json"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("name", stdout, StringComparison.Ordinal);
        Assert.Contains("sessions", stdout, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ----- create -----

    [Fact]
    public async Task WorkspaceCreate_SendsPostWithNameAndRepos()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "create", "my-ws", "--repo", "server", "--repo", "web"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/workspaces", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("my-ws", body["name"]?.GetValue<string>());
        Assert.Equal(new[] { "server", "web" }, body["repos"]?.AsArray()?.Select(n => n!.GetValue<string>())!);
    }

    [Fact]
    public async Task WorkspaceCreate_WithoutRepos_SendsEmptyRepos()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "create", "my-ws"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("my-ws", body["name"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("repos"));
    }

    // ----- close -----

    [Fact]
    public async Task WorkspaceClose_SendsPostToCloseEndpoint()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "close", "my-ws"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/workspaces/my-ws/close", request.RequestUri?.PathAndQuery);
    }

    // ----- repo add -----

    [Fact]
    public async Task WorkspaceRepoAdd_SendsPostWithRepoBody()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "repo", "add", "my-ws", "server"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/workspaces/my-ws/repo", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("server", body["repo"]?.GetValue<string>());
    }

    // ----- repo remove -----

    [Fact]
    public async Task WorkspaceRepoRemove_SendsDeleteWithRepoQuery()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "repo", "remove", "my-ws", "server"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/projects/proj_test/workspaces/my-ws/repo?repo=server", request.RequestUri?.PathAndQuery);
    }

    // ----- project resolution -----

    [Fact]
    public async Task WorkspaceCommands_RequireProject()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WorkspaceList_WithProjectFlag_UsesResolvedProject()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list", "--project", "proj_by_name"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_by_name/workspaces", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    // ----- error handling -----

    [Fact]
    public async Task WorkspaceCreate_NameTaken_ShowsError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(req =>
            RecordingHttpHandler.JsonError("Workspace name 'my-ws' is already taken", "workspace_name_taken", HttpStatusCode.Conflict));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "create", "my-ws"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WorkspaceClose_WithActiveSessions_ShowsError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(req =>
            RecordingHttpHandler.JsonError("Workspace has 2 active sessions", "workspace_has_active_sessions", HttpStatusCode.Conflict));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "close", "my-ws"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
    }

    // ----- agent launch --workspace -----

    [Fact]
    public async Task AgentLaunch_WithWorkspace_SendsWorkspaceInContext()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(req =>
            RecordingHttpHandler.Json(new
            {
                jobId = "job_1",
                sessionId = "sess_1",
                inputId = "input_1",
                turnId = "turn_1",
                agentId = "agent_abc",
                agentName = "test-agent",
                workspaceId = "pay",
                targetId = "agent_abc",
                origin = "cli",
                status = "running",
                attachments = (object?)null,
                rejectedAttachments = (object?)null,
                transcriptUrl = "/transcript",
                jobUrl = "/job",
                observationUrl = "/obs",
                sessionUrl = "/Test/sessions/sess_1",
            }));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "launch", "test-agent", "--prompt", "hello", "--workspace", "pay"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        var body = JsonNode.Parse(request.Body!)!;
        var ctx = body["context"];
        Assert.NotNull(ctx);
        Assert.Equal("pay", ctx!["workspace"]?.GetValue<string>());
        Assert.False(ctx.AsObject().ContainsKey("workspacePath"));
        Assert.Equal("cli", request.Headers["X-Mohist-Launch-Origin"].Single());
    }

    [Fact]
    public async Task AgentLaunch_WithoutWorkspace_UsesServerDefaultWorkspaceInResponse()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(req =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    jobId = "job_1",
                    sessionId = "sess_1",
                    inputId = "input_1",
                    turnId = "turn_1",
                    agentId = "agent_abc",
                    agentName = "test-agent",
                    workspaceId = "cli-current",
                    targetId = "agent_abc",
                    origin = "cli",
                    status = "running",
                    attachments = (object?)null,
                    rejectedAttachments = (object?)null,
                    transcriptUrl = "/transcript",
                    jobUrl = "/job",
                    observationUrl = "/obs",
                    sessionUrl = "/Test/sessions/sess_1",
                },
            }));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "launch", "test-agent", "--prompt", "hello"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        var body = JsonNode.Parse(request.Body!)!;
        Assert.False(body.AsObject().ContainsKey("context"));
        Assert.Equal("cli", request.Headers["X-Mohist-Launch-Origin"].Single());
        Assert.Contains("cli-current", output.ToString(), StringComparison.Ordinal);
    }

    // ----- session list --workspace -----

    [Fact]
    public async Task SessionList_WithWorkspace_SendsWorkspaceQuery()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(req =>
            RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--workspace", "pay"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_test/sessions?workspace=pay", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionList_MultipleSelectors_IsRejected()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "list", "--agent", "foo", "--workspace", "bar"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ----- Json field selection -----

    [Fact]
    public async Task WorkspaceList_JsonSelection_ReturnsOnlyRequestedFields()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync(req =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { projectId = "proj_1", name = "ws1", origin = new { kind = "manual" }, repositories = Array.Empty<string>(), status = "active", home = (object?)null, createdAt = "2025-01-01T00:00:00Z", archivedAt = (string?)null, sessions = (object?)null },
                },
            }));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list", "--json", "name,status"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        var parsed = JsonNode.Parse(stdout);
        Assert.NotNull(parsed);
        var arr = parsed!.AsArray();
        Assert.Single(arr);
        var obj = arr[0]!.AsObject();
        Assert.Equal(2, obj.Count);
        Assert.Equal("ws1", obj["name"]?.GetValue<string>());
        Assert.Equal("active", obj["status"]?.GetValue<string>());
        Assert.False(obj.ContainsKey("origin"));
    }

    [Fact]
    public async Task WorkspaceList_InvalidJsonField_ReturnsError()
    {
        var (http, handler, output, error, fileSystem, executor) = SetupSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list", "--json", "nonexistent"], output, error, fileSystem, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}
