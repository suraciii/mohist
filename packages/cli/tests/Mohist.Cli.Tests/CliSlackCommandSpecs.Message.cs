using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed partial class CliSlackCommandSpecs
{
    [Fact]
    public async Task MessageSend_PostsAgentReplyToTheReplyEndpoint()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { accepted = true, connectionId = "connection_1", deliveryId = "slkout_1", dispatchRef = "slack-reply:connection_1:C1:1710000000.000100:terminal", merged = false },
            })));

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "message", "send", "--workspace", "T1", "--conversation", "C1", "--reply-to", "1710000000.000100", "--connection", "connection_1", "--session", "session-1", "--triggering-message", "1710000000.000200", "--dispatch-ref", "agent-session-followup:session-1:turn-2", "--text", "All green. token=xoxb-leak"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/slack-connections/reply", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("C1", body["conversationId"]!.GetValue<string>());
        Assert.Equal("1710000000.000100", body["threadTs"]!.GetValue<string>());
        Assert.Equal("T1", body["workspaceTeamId"]!.GetValue<string>());
        Assert.Equal("session-1", body["sessionId"]!.GetValue<string>());
        Assert.Equal("connection_1", body["connectionId"]!.GetValue<string>());
        Assert.Equal("1710000000.000200", body["triggeringMessageId"]!.GetValue<string>());
        Assert.Equal("agent-session-followup:session-1:turn-2", body["dispatchRef"]!.GetValue<string>());
        // The CLI forwards the Agent-authored body verbatim; the Server redacts.
        Assert.Equal("All green. token=xoxb-leak", body["text"]!.GetValue<string>());
        Assert.Contains("accepted", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageSend_rejects_anchorless_agent_reply_before_http()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(http,
            ["slack", "message", "send", "--conversation", "C1", "--text", "hello"],
            output, error, fs, executor);

        Assert.NotEqual(0, exit);
        Assert.Empty(handler.Requests);
        Assert.Contains("anchored-reply contract", error.ToString(), StringComparison.Ordinal);
    }

    private static string[] ReplyAnchorArgs() =>
    [
        "--workspace", "T1",
        "--reply-to", "1710000000.000100",
        "--connection", "connection_1",
        "--session", "session-1",
        "--triggering-message", "1710000000.000200",
        "--dispatch-ref", "agent-session-followup:session-1:turn-2",
    ];
}
