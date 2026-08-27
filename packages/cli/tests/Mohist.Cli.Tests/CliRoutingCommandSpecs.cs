using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliRoutingCommandSpecs
{
    [Fact]
    public async Task RuleCreate_WithBefore_ResolvesAgentThenPostsScopedRuleAndPosition()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(ResponderWithAgent("agent_a", "a"));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "create", "--name", "C", "--match", "event.type == \"x\"", "--agent", "agent_a", "--response-prompt", "hello", "--before", "rule_a", "--project", "proj_test"], output, error, fs, executor);

        Assert.True(exit == 0, $"exit={exit}, error={error}");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/projects/proj_test/agents/agent_a", handler.Requests[0].RequestUri?.PathAndQuery);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/routing/rules?before=rule_a", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("C", body["name"]!.GetValue<string>());
        Assert.Equal("agent_a", body["agentId"]!.GetValue<string>());
        Assert.False(body["continue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RuleCreate_ResolvesAgentNameAndSendsExactPropertySet()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(ResponderWithAgent("agent_123", "reviewer"));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "create", "--name", "C", "--match", "event.type == \"x\"", "--agent", "reviewer", "--response-prompt", "hello", "--project", "proj_test"], output, error, fs, executor);

        Assert.True(exit == 0, $"exit={exit}, error={error}");
        Assert.Equal("/api/projects/proj_test/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("agent_123", body["agentId"]!.GetValue<string>());
        Assert.Equal(
            new[] { "agentId", "continue", "match", "name", "responsePrompt" },
            body.Select(property => property.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("reviewer", "/api/projects/proj_test/agents?all=true")]
    [InlineData("agent_123", "/api/projects/proj_test/agents/agent_123")]
    public async Task RuleCreate_NameAndIdAgentInputsSendTheSameStableAgentId(string agentInput, string resolutionPath)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(ResponderWithAgent("agent_123", "reviewer"));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "create", "--name", "C", "--match", "event.type == \"x\"", "--agent", agentInput, "--response-prompt", "hello", "--project", "proj_test"], output, error, fs, executor);

        Assert.True(exit == 0, $"exit={exit}, error={error}");
        Assert.Equal(resolutionPath, handler.Requests[0].RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("agent_123", body["agentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task RuleCreate_UnknownAgentFailsBeforeMutation()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));
            throw new InvalidOperationException($"unexpected mutation request: {path}");
        });

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "create", "--name", "C", "--match", "event.type == \"x\"", "--agent", "missing", "--response-prompt", "hello", "--project", "proj_test"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("Agent 'missing' not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleEdit_SendsAgentResolutionThenOnlySuppliedProperties()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(ResponderWithAgent("agent_123", "reviewer"));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "edit", "rule_1", "--agent", "reviewer", "--continue", "false", "--project", "proj_test"], output, error, fs, executor);

        Assert.True(exit == 0, $"exit={exit}, error={error}");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/projects/proj_test/agents?all=true", handler.Requests[0].RequestUri?.PathAndQuery);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/projects/proj_test/routing/rules/rule_1", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("agent_123", body["agentId"]!.GetValue<string>());
        Assert.False(body["continue"]!.GetValue<bool>());
        Assert.Equal(
            new[] { "agentId", "continue" },
            body.Select(property => property.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task RuleEdit_WithoutAgentResolution_SendsExactSuppliedSet()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(ResponderWithAgent("agent_123", "reviewer"));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "edit", "rule_1", "--name", "Renamed", "--project", "proj_test"], output, error, fs, executor);

        Assert.True(exit == 0, $"exit={exit}, error={error}");
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("Renamed", body["name"]!.GetValue<string>());
        Assert.Equal(new[] { "name" }, body.Select(property => property.Key).ToArray());
    }

    [Fact]
    public async Task RuleEdit_UnknownAgentFailsBeforeMutation()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));
            throw new InvalidOperationException($"unexpected mutation request: {path}");
        });

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "edit", "rule_1", "--agent", "missing", "--project", "proj_test"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("Agent 'missing' not found", error.ToString(), StringComparison.Ordinal);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ResponderWithAgent(string agentId, string agentName) =>
        (request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/agents?all=true", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new[] { new { id = agentId, name = agentName } } }));
            if (path.EndsWith($"/agents/{agentId}", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = agentId, name = agentName } }));
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "rule_1", name = "C" } }));
        };

    [Fact]
    public async Task RuleList_RendersServerOrder()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new JsonArray(
                new JsonObject { ["position"] = 1, ["name"] = "A", ["agentId"] = "a", ["status"] = "active", ["continue"] = false },
                new JsonObject { ["position"] = 2, ["name"] = "B", ["agentId"] = "b", ["status"] = "active", ["continue"] = true }),
        })));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "list", "--project", "proj_test",], output, error, fs, executor);

        Assert.True(exit == 0, $"exit={exit}, error={error}");
        var text = output.ToString();
        Assert.True(text.IndexOf("A", StringComparison.Ordinal) < text.IndexOf("B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuleMove_WithAfter_SendsTargetAndPosition()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } })));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "move", "rule_a", "--after", "rule_c", "--project", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_test/routing/rules/rule_a/move", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("rule_c", body["after"]!.GetValue<string>());
    }

    [Fact]
    public async Task RoutingTest_RendersTraceAndDefaultLastIsOmitted()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new { events = new[] { new { eventId = "event_1", outcomes = new[] { new { ruleName = "approval", decision = "would-trigger", agentName = "reviewer" } } } } },
        })));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "test", "--project", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_test/routing/test", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
        Assert.Contains("event_1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("approval", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("reviewer", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutingTest_LimitIsForwardedAsQueryParameter()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { events = Array.Empty<object>() } })));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "test", "--project", "proj_test", "--limit", "42"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_test/routing/test?limit=42", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RoutingCommand_WithoutProject_FailsLocally()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString(), StringComparison.Ordinal);
    }
}
