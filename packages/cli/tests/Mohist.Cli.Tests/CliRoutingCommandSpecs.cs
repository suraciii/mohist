using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliRoutingCommandSpecs
{
    [Fact]
    public async Task RuleCreate_WithBefore_SendsProjectScopedRuleAndPosition()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "rule_c", position = 1 } }, HttpStatusCode.Created)));

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "create", "--name", "C", "--match", "event.type == \"x\"", "--agent", "agent_a", "--response-prompt", "hello", "--before", "rule_a", "--project-id", "proj_test"], output, error, fs, executor);

        Assert.True(exit == 0, $"exit={exit}, error={error}");
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_test/routing/rules?before=rule_a", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("C", body["name"]!.GetValue<string>());
        Assert.False(body["continue"]!.GetValue<bool>());
    }

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

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "rule", "list", "--project-id", "proj_test", "--output", "table"], output, error, fs, executor);

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

        var exit = await MohistCliCommands.RunAsync(http, ["routing", "test", "--project-id", "proj_test"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_test/routing/test", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
        Assert.Contains("event_1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("approval", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("reviewer", output.ToString(), StringComparison.Ordinal);
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
