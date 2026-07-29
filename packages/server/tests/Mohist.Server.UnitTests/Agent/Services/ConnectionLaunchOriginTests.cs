using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class ConnectionLaunchOriginTests
{
    [Fact]
    public void CoordinatorKeyUsesStableSlackMessageIdentity()
    {
        var key = AgentLaunchCoordinatorCodec.KeyFor("project", "slack:T1:D1:1712345678.0001");

        Assert.Equal("agent-launch-coord/project/slack:T1:D1:1712345678.0001", key);
    }

    [Fact]
    public void ConnectionOriginChangesReplayFingerprint()
    {
        var request = new AgentLaunchCoordinatorRequest("task", "agent", null, null, null, null, null, null);
        var first = new ConnectionLaunchOrigin("connection", "T1", "U1", "D1", "1.0");
        var second = first with { MessageTs = "2.0" };

        Assert.NotEqual(
            AgentLaunchCoordinatorCodec.Fingerprint(request, first),
            AgentLaunchCoordinatorCodec.Fingerprint(request, second));
    }

    [Fact]
    public void MessageIdentityChangesCoordinatorKeyForEverySlackCoordinate()
    {
        const string projectId = "project";
        var first = AgentLaunchCoordinatorCodec.KeyFor(projectId, "slack:T1:D1:1.0");

        Assert.NotEqual(first, AgentLaunchCoordinatorCodec.KeyFor(projectId, "slack:T2:D1:1.0"));
        Assert.NotEqual(first, AgentLaunchCoordinatorCodec.KeyFor(projectId, "slack:T1:D2:1.0"));
        Assert.NotEqual(first, AgentLaunchCoordinatorCodec.KeyFor(projectId, "slack:T1:D1:2.0"));
        Assert.NotEqual(first, AgentLaunchCoordinatorCodec.KeyFor("other-project", "slack:T1:D1:1.0"));
    }
}
