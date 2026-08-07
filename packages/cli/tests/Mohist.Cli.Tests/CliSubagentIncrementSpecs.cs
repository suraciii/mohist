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
                    jobId = "job_child",
                    sessionId = "sess_child",
                    inputId = "input_child",
                    turnId = "turn_child",
                    agentId = "agent_child",
                    agentName = "Child",
                    parentSessionId = "sess_parent",
                    edgeId = "edge_1",
                    status = "queued",
                    transcriptUrl = "/transcript",
                    jobUrl = "/job",
                    observationUrl = "/observation",
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
        Assert.Equal(
            "job id:         job_child\nsession id:     sess_child\nturn id:        turn_child\nparent session: sess_parent\nedge id:        edge_1\n",
            output.ToString());
        Assert.Equal(
            ["jobId", "sessionId", "turnId", "parentSessionId", "edgeId"],
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentSessionSpawn)).Fields);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task AgentSpawn_JsonSelectionUsesTheFiveFieldSchema()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    jobId = "job_child",
                    sessionId = "sess_child",
                    inputId = "input_child",
                    turnId = "turn_child",
                    agentId = "agent_child",
                    agentName = "Child",
                    status = "queued",
                    parentSessionId = "sess_parent",
                    edgeId = "edge_1",
                    transcriptUrl = "/transcript",
                    jobUrl = "/job",
                    observationUrl = "/observation",
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
                "--json", "jobId,sessionId,turnId,parentSessionId,edgeId",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var data = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal(
            ["jobId", "sessionId", "turnId", "parentSessionId", "edgeId"],
            data.Select(property => property.Key));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task AgentSpawn_WorkspaceModeRetired_RejectedWithoutRequest()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "agent", "spawn", "agent_child",
                "--project", "proj_parent",
                "--parent-session", "sess_parent",
                "--prompt", "do the work",
                "--idempotency-key", "spawn-1",
                "--workspace", "worktree",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--workspace was retired: child sessions always inherit the parent workdir", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentSpawn_SameKeyDifferentPrompt_SurfacesIdempotencyConflict()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.JsonError(
                "spawn request conflicts with an earlier request using the same idempotency key",
                "spawn_idempotency_conflict",
                HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "agent", "spawn", "agent_child",
                "--project", "proj_parent",
                "--parent-session", "sess_parent",
                "--prompt", "do other work",
                "--idempotency-key", "spawn-1",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(1, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("spawn-1", request.Headers["Idempotency-Key"].Single());
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.False(body.ContainsKey("workspace"));
        Assert.Contains("spawn_idempotency_conflict", error.ToString(), StringComparison.Ordinal);
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
    public async Task SessionTree_WithExplicitProject_UsesServerOrderAndLockedQuery()
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

    [Fact]
    public async Task SessionTree_UsesActiveProjectWhenProjectIsOmitted()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    root = new { sessionId = "sess_root" },
                    revision = "rev_active",
                    nodes = Array.Empty<object>(),
                    edges = Array.Empty<object>(),
                    continuation = (string?)null,
                },
            })),
            activeProjectId: "proj_active");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["session", "tree", "sess_root", "--json", "root,revision,nodes,edges,continuation"],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/projects/proj_active/agent-sessions/sess_root/tree",
            request.RequestUri?.PathAndQuery);
        var data = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("rev_active", data["revision"]!.GetValue<string>());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void SessionTree_OutputCatalogMatchesLockedContract()
    {
        Assert.Equal(
            ["root", "revision", "nodes", "edges", "continuation"],
            ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionTree)).Fields);
    }

    [Fact]
    public async Task SessionStop_SendsOnlyIdempotencyKeyAndNoSnapshotInputs()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    operationId = "stop-op-1",
                    rootSessionId = "sess_root",
                    status = "unknown",
                    graphRevision = 7,
                    membership = new[] { new { sessionId = "sess_root" } },
                    targets = new[] { new { sessionId = "sess_root", outcome = "unknown" } },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "session", "stop", "sess_root",
                "--project", "proj_parent",
                "--idempotency-key", "stop-key-1",
                "--json", "operationId,status",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "/api/projects/proj_parent/agent-sessions/sess_root/stop",
            request.RequestUri?.PathAndQuery);
        Assert.Equal("stop-key-1", request.Headers["Idempotency-Key"].Single());
        Assert.True(string.IsNullOrEmpty(request.Body));
        Assert.Equal("stop-op-1", JsonNode.Parse(output.ToString())!["operationId"]!.GetValue<string>());
        Assert.Equal("unknown", JsonNode.Parse(output.ToString())!["status"]!.GetValue<string>());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task SessionDetach_PostsOnlyChildSessionIdAndRendersHistoricTuple()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    childSessionId = "sess_child",
                    parentSessionId = "sess_parent",
                    edgeId = "edge_1",
                    childLaunchJobId = "job_child",
                    attachedRevision = 3,
                    detachedRevision = 8,
                    historic = true,
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "session", "detach", "sess_child",
                "--project", "proj_parent",
                "--json", "childSessionId,parentSessionId,edgeId,detachedRevision",
            ],
            output,
            error,
            fileSystem,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "/api/projects/proj_parent/agent-sessions/sess_child/detach",
            request.RequestUri?.PathAndQuery);
        Assert.True(string.IsNullOrEmpty(request.Body));
        var data = JsonNode.Parse(output.ToString())!;
        Assert.Equal("sess_child", data["childSessionId"]!.GetValue<string>());
        Assert.Equal("sess_parent", data["parentSessionId"]!.GetValue<string>());
        Assert.Equal("edge_1", data["edgeId"]!.GetValue<string>());
        Assert.Equal(8, data["detachedRevision"]!.GetValue<long>());
        Assert.Empty(error.ToString());
    }
}
