using System.Text.Json;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentLauncherResolveRuntimeTests
{
    [Fact]
    public void ResolveRuntime_UsesAgentRuntime()
    {
        var config = JsonDocument.Parse("{\"runtime\":\"pi\"}").RootElement;

        Assert.Equal("pi", AgentLauncher.ResolveRuntime(config));
    }

    [Fact]
    public void ResolveRuntime_DefaultsToOpenCode()
    {
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(null));
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(JsonDocument.Parse("{\"runtime\":\"mystery\"}").RootElement));
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(JsonDocument.Parse("{\"runtime\":42}").RootElement));
    }
}
