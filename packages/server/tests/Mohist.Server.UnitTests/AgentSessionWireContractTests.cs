using System.Text.Json;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests;

public sealed class AgentSessionWireContractTests
{
    [Fact]
    public void TreePage_ContainsCliRootObjectAndIndependentEdges()
    {
        var page = new AgentSessionTreePage(
            new AgentSessionTreeRoot("session-root"),
            7,
            [],
            [new AgentSessionTreeEdge("edge-1", "session-root", "session-child", "job-1", "attached")],
            "cursor");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(page, JSON.Options));
        var root = document.RootElement;
        Assert.Equal(
            ["root", "revision", "nodes", "edges", "continuation"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("session-root", root.GetProperty("root").GetProperty("sessionId").GetString());
        var edge = Assert.Single(root.GetProperty("edges").EnumerateArray().ToArray());
        Assert.Equal("edge-1", edge.GetProperty("edgeId").GetString());
        Assert.Equal("session-root", edge.GetProperty("parentSessionId").GetString());
        Assert.Equal("session-child", edge.GetProperty("childSessionId").GetString());
        Assert.Equal("job-1", edge.GetProperty("childLaunchJobId").GetString());
        Assert.Equal("attached", edge.GetProperty("state").GetString());
    }

    [Fact]
    public void SpawnResponse_UsesAcceptedLaunchFieldsAndAddsParentIdentity()
    {
        var response = new AgentSessionSpawnRoutes.AgentSessionSpawnResponse(
            "job-1",
            "session-child",
            "input-1",
            "turn-1",
            "agent-child",
            "Child",
            "queued",
            null,
            null,
            "/api/projects/proj-parent/agent-sessions/session-child/transcript",
            "/api/projects/proj-parent/agent-jobs/job-1",
            "/api/projects/proj-parent/agent-jobs/job-1/launch-observation",
            "session-parent",
            "edge-1");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, JSON.Options));
        var root = document.RootElement;
        Assert.Equal("queued", root.GetProperty("status").GetString());
        Assert.Equal("/api/projects/proj-parent/agent-jobs/job-1", root.GetProperty("jobUrl").GetString());
        Assert.Equal("session-parent", root.GetProperty("parentSessionId").GetString());
        Assert.Equal("edge-1", root.GetProperty("edgeId").GetString());
    }
}
