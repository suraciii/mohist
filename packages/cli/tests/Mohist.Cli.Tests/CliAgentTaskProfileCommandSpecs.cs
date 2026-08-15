using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public partial class CliAgentCommandSpecs
{
    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public async Task AgentCreate_EmptyPermissionsFailsBeforeHttp(string permissions)
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "create", "--name", "reviewer", "--instructions", "Review", "--permissions", permissions],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("--permissions must contain at least one non-empty permission term", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public async Task AgentUpdate_EmptyPermissionsFailsBeforeHttp(string permissions)
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--permissions", permissions],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("--permissions must contain at least one non-empty permission term", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentView_PrintsTaskProfileAndServerReadiness()
    {
        var handler = new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new[] { Agent("agent_123", "reviewer") } }));
            if (path.EndsWith("/agents/agent_123", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "agent_123",
                        name = "reviewer",
                        status = "active",
                        purpose = "Review repository changes",
                        description = "desc",
                        instructions = "prompt",
                        agentConfig = new { model = "openai/gpt-5.5" },
                        skills = new[] { "mohist" },
                        permissions = new[] { "repo:read", "artifact:publish" },
                        maxConcurrentRuns = 2,
                        createdAt = "2026-06-18T01:00:00Z",
                        updatedAt = "2026-06-18T01:00:00Z",
                        readiness = new
                        {
                            conclusion = "Needs setup",
                            gaps = new[]
                            {
                                new { code = "instructions-missing", message = "Instructions are missing.", action = "Add instructions in Agent settings." },
                            },
                            setup = new { label = "Agent settings", path = "/agents/agent_123/settings" },
                        },
                    },
                }));
            return Task.FromResult(RecordingHttpHandler.JsonError("not found", statusCode: HttpStatusCode.NotFound));
        });
        var output = new StringWriter();

        var exitCode = await RunAsync(handler, ["agent", "view", "reviewer"], output: output, fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("readiness:           Needs setup", text, StringComparison.Ordinal);
        Assert.Contains("purpose:             Review repository changes", text, StringComparison.Ordinal);
        Assert.Contains("permissions:         repo:read,artifact:publish", text, StringComparison.Ordinal);
        Assert.Contains("Instructions are missing", text, StringComparison.Ordinal);
        Assert.Contains("Add instructions in Agent settings", text, StringComparison.Ordinal);
        Assert.Contains("readiness setup:     Agent settings (/agents/agent_123/settings)", text, StringComparison.Ordinal);
    }
}
