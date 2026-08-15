using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public partial class CliAgentCommandSpecs
{
    [Fact]
    public async Task AgentCreate_SendsPurposeAndDeclaredPermissions()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = ProfileAgent(),
        }, HttpStatusCode.Created)));

        var exitCode = await RunAsync(
            handler,
            ["agent", "create", "--name", "reviewer", "--instructions", "Review strictly", "--purpose", "Review pull requests", "--permissions", "repo:read,artifact:publish"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("Review pull requests", body["purpose"]?.GetValue<string>());
        Assert.Equal("repo:read", body["permissions"]?[0]?.GetValue<string>());
        Assert.Equal("artifact:publish", body["permissions"]?[1]?.GetValue<string>());
    }

    [Fact]
    public async Task AgentEdit_UpdatesAndClearsPurposeAndPermissions()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get ? new[] { ProfileAgent() } : ProfileAgent(),
        })));

        var updateExit = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--purpose", "Validate releases", "--permissions", "repo:write,issue:write"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, updateExit);
        var updateBody = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal("Validate releases", updateBody["purpose"]?.GetValue<string>());
        Assert.Equal("repo:write", updateBody["permissions"]?[0]?.GetValue<string>());
        Assert.Equal("issue:write", updateBody["permissions"]?[1]?.GetValue<string>());

        var clearExit = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--clear-purpose", "--clear-permissions"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, clearExit);
        var clearBody = JsonNode.Parse(handler.Requests[3].Body!)!;
        Assert.True(clearBody.AsObject().ContainsKey("purpose"));
        Assert.Null(clearBody["purpose"]);
        Assert.Empty(clearBody["permissions"]!.AsArray());
    }

    [Fact]
    public async Task AgentEdit_RejectsConflictingPermissionSetAndClearFlags()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--permissions", "repo:read", "--clear-permissions"],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("--permissions cannot be used with --clear-permissions", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentView_RendersPersistedPurposeAndPermissions()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery.EndsWith("/agents?all=true", StringComparison.Ordinal)
                ? new[] { ProfileAgent() }
                : ProfileAgent(),
        })));
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "view", "reviewer"], output: output, fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        Assert.Contains("purpose:             Review pull requests", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("permissions:         repo:read,artifact:publish", output.ToString(), StringComparison.Ordinal);
    }

    private static object ProfileAgent() => new
    {
        id = "agent_123",
        projectId = "proj_123",
        name = "reviewer",
        purpose = "Review pull requests",
        description = "Reviews changes with a release focus.",
        instructions = "Review strictly.",
        agentConfig = new { runtime = "opencode", model = "openai/gpt-5.5", variant = "high" },
        skills = new[] { "mohist" },
        permissions = new[] { "repo:read", "artifact:publish" },
        maxConcurrentRuns = 2,
        status = "active",
        createdAt = "2026-08-15T01:00:00Z",
        updatedAt = "2026-08-15T01:00:00Z",
    };
}
