using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

/// <summary>
/// Unit tests for <see cref="AgentLauncher.ResolveRuntime"/> — the
/// resolution helper that fixes the execution backend at launch time
/// (issue-452 design D2). The resolver implements
/// <c>launchOverride ?? agentConfig.runtime ?? "opencode"</c> and is
/// shared by the manual launch path (passes the request override), the
/// routed launch path (passes no override), and the routed preflight
/// (passes no override). Editing the Agent's runtime config after
/// launch cannot affect the snapshotted result because the helper runs
/// only at the launcher level.
/// </summary>
public class AgentLauncherResolveRuntimeTests
{
    [Fact]
    public void ResolveRuntime_OverrideWinsOverAgentConfig()
    {
        var config = JsonDocument.Parse("""{"runtime":"pi"}""").RootElement;

        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(config, "opencode"));
    }

    [Fact]
    public void ResolveRuntime_AbsentOverride_FallsBackToAgentConfig()
    {
        var config = JsonDocument.Parse("""{"runtime":"pi"}""").RootElement;

        Assert.Equal("pi", AgentLauncher.ResolveRuntime(config, null));
    }

    [Fact]
    public void ResolveRuntime_AbsentOverrideAndAbsentConfig_DefaultsToOpenCode()
    {
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(null, null));
    }

    [Fact]
    public void ResolveRuntime_BlankOverride_FallsBackToAgentConfig()
    {
        var config = JsonDocument.Parse("""{"runtime":"pi"}""").RootElement;

        Assert.Equal("pi", AgentLauncher.ResolveRuntime(config, " "));
    }

    [Fact]
    public void ResolveRuntime_UnknownOverrideIgnored_FallsBackToAgentConfig()
    {
        var config = JsonDocument.Parse("""{"runtime":"pi"}""").RootElement;

        Assert.Equal("pi", AgentLauncher.ResolveRuntime(config, "mystery"));
    }

    [Fact]
    public void ResolveRuntime_UnknownOverrideWithoutConfig_DefaultsToOpenCode()
    {
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(null, "mystery"));
    }

    [Fact]
    public void ResolveRuntime_BlankConfigAndNoOverride_DefaultsToOpenCode()
    {
        var config = JsonDocument.Parse("""{"runtime":""}""").RootElement;

        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(config, null));
    }

    [Fact]
    public void ResolveRuntime_NonStringConfigRuntime_FallsBackToOpenCode()
    {
        var config = JsonDocument.Parse("""{"runtime":42}""").RootElement;

        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(config, null));
    }

    [Fact]
    public void ResolveRuntime_UnknownConfigRuntime_FallsBackToOpenCode()
    {
        var config = JsonDocument.Parse("""{"runtime":"mystery"}""").RootElement;

        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(config, null));
    }

    [Fact]
    public void ResolveRuntime_NonObjectConfig_DefaultsToOpenCode()
    {
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(JsonDocument.Parse("\"foo\"").RootElement, null));
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(JsonDocument.Parse("null").RootElement, null));
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(JsonDocument.Parse("[]").RootElement, null));
    }
}