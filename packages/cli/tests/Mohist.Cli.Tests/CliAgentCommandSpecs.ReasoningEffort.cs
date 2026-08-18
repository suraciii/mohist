using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public partial class CliAgentCommandSpecs
{
    [Fact]
    public async Task AgentCreate_SendsCanonicalReasoningEffort()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Agent("agent_123", "reviewer"),
        }, HttpStatusCode.Created)));

        var exitCode = await RunAsync(handler,
            ["agent", "create", "--name", "reviewer", "--instructions", "Review strictly", "--runtime", "pi", "--model", "openai/gpt-5.5", "--reasoning-effort", "high"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("high", body["agentConfig"]?["reasoningEffort"]?.GetValue<string>());
    }

    [Fact]
    public async Task AgentCreate_EmptyReasoningEffortFailsBeforeHttp()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new InvalidOperationException("API must not be called"));
        var error = new StringWriter();

        var exitCode = await RunAsync(
            handler,
            ["agent", "create", "--name", "reviewer", "--instructions", "Review", "--reasoning-effort", " "],
            error: error,
            fileSystem: FileSystemWithProject());

        Assert.Equal(2, exitCode);
        Assert.Contains("--reasoning-effort must not be empty", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentUpdate_SetsAndClearsReasoningEffortWithoutChangingOtherConfig()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer", updatedAt: "2026-06-18T02:00:00Z"),
        })));

        var setExit = await RunAsync(handler,
            ["agent", "edit", "reviewer", "--reasoning-effort", "xhigh"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, setExit);
        var setBody = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("opencode", setBody["agentConfig"]?["runtime"]?.GetValue<string>());
        Assert.Equal("openai/gpt-5.5", setBody["agentConfig"]?["model"]?.GetValue<string>());
        Assert.Equal("high", setBody["agentConfig"]?["variant"]?.GetValue<string>());
        Assert.Equal("xhigh", setBody["agentConfig"]?["reasoningEffort"]?.GetValue<string>());

        var clearExit = await RunAsync(handler,
            ["agent", "edit", "reviewer", "--clear-reasoning-effort"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, clearExit);
        var clearBody = JsonNode.Parse(handler.Requests[3].Body!)!.AsObject();
        Assert.Equal("opencode", clearBody["agentConfig"]?["runtime"]?.GetValue<string>());
        Assert.Equal("openai/gpt-5.5", clearBody["agentConfig"]?["model"]?.GetValue<string>());
        Assert.Equal("high", clearBody["agentConfig"]?["variant"]?.GetValue<string>());
        Assert.Null(clearBody["agentConfig"]?["reasoningEffort"]);
    }

    [Fact]
    public async Task AgentUpdate_SetsReasoningEffortAlongsideExistingKeys()
    {
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.Method == HttpMethod.Get
                ? new[] { Agent("agent_123", "reviewer") }
                : Agent("agent_123", "reviewer", updatedAt: "2026-06-18T02:00:00Z"),
        })));

        var exitCode = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--reasoning-effort", "xhigh"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        var agentConfig = JsonNode.Parse(handler.Requests[1].Body!)!["agentConfig"]!.AsObject();
        Assert.Equal("xhigh", agentConfig["reasoningEffort"]?.GetValue<string>());
        // Existing typed keys are carried over untouched.
        Assert.Equal("opencode", agentConfig["runtime"]?.GetValue<string>());
        Assert.Equal("openai/gpt-5.5", agentConfig["model"]?.GetValue<string>());
        Assert.Equal("high", agentConfig["variant"]?.GetValue<string>());
    }

    [Fact]
    public async Task AgentUpdate_ClearReasoningEffortRemovesOnlyEffortKey()
    {
        object ExistingAgent() => new
        {
            id = "agent_123",
            projectId = "proj_123",
            name = "reviewer",
            instructions = "prompt",
            agentConfig = new { runtime = "pi", model = "openai/gpt-5.5", variant = "balanced", reasoningEffort = "high" },
            createdAt = "2026-06-18T01:00:00Z",
            updatedAt = "2026-06-18T01:00:00Z",
        };
        var handler = new RecordingHttpHandler((request, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = request.RequestUri?.PathAndQuery.EndsWith("/agents?all=true") == true
                ? new[] { ExistingAgent() }
                : ExistingAgent(),
        })));

        var exitCode = await RunAsync(
            handler,
            ["agent", "edit", "reviewer", "--clear-reasoning-effort"],
            fileSystem: FileSystemWithProject());

        Assert.Equal(0, exitCode);
        var agentConfig = JsonNode.Parse(handler.Requests[1].Body!)!["agentConfig"]!.AsObject();
        // The clear removes only the effort key: runtime, model, and variant survive.
        Assert.False(agentConfig.ContainsKey("reasoningEffort"));
        Assert.Equal("pi", agentConfig["runtime"]?.GetValue<string>());
        Assert.Equal("openai/gpt-5.5", agentConfig["model"]?.GetValue<string>());
        Assert.Equal("balanced", agentConfig["variant"]?.GetValue<string>());
    }
}
