using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliSubagentIncrementSpecs
{
    [Fact]
    public async Task AgentSpawn_SendsOnlyLockedBodyAndIdempotencyHeader()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    sessionId = "sess_child",
                    parentSessionId = "sess_parent",
                    edgeId = "edge_1",
                    status = "queued",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "agent", "spawn", "agent_child",
                "--project", "proj_parent",
                "--parent-session", "sess_parent",
                "--prompt", "do the work",
                "--idempotency-key", "spawn-1",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "/api/projects/proj_parent/agent-sessions/sess_parent/spawns",
            request.RequestUri?.PathAndQuery);
        Assert.Equal("spawn-1", request.Headers["Idempotency-Key"].Single());
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("agent_child", body["targetAgentRef"]!.GetValue<string>());
        Assert.Equal("do the work", body["prompt"]!.GetValue<string>());
        Assert.Equal(2, body.Count);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task AgentCreate_RepeatsAllowedSubagentIdsAsOneRequestField()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "agent_parent" },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "agent", "create",
                "--name", "parent",
                "--instructions", "delegate",
                "--project", "proj_parent",
                "--allowed-subagent", "agent_child_a",
                "--allowed-subagent", "agent_child_b",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal(
            ["agent_child_a", "agent_child_b"],
            body["allowedSubagentAgentIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task SessionTree_UsesServerOrderAndLockedQuery()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    root = new { sessionId = "sess_root" },
                    revision = "rev_7",
                    nodes = new[] { new { sessionId = "sess_root" }, new { sessionId = "sess_child" } },
                    edges = new[] { new { edgeId = "edge_1" } },
                    continuation = "next-token",
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "session", "tree", "sess_root",
                "--project", "proj_parent",
                "--limit", "10",
                "--continuation", "page one",
                "--json", "root,revision,nodes,edges,continuation",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/projects/proj_parent/agent-sessions/sess_root/tree?limit=10&continuation=page%20one",
            request.RequestUri?.PathAndQuery);
        var data = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("sess_root", data["nodes"]![0]!["sessionId"]!.GetValue<string>());
        Assert.Equal("sess_child", data["nodes"]![1]!["sessionId"]!.GetValue<string>());
        Assert.Empty(error.ToString());
    }
}
