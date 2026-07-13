using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public class AgentLauncherTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public async Task LaunchAsync_BlankPrompt_RejectsBeforeUsingDependencies(string prompt)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateLauncherForValidation().LaunchAsync(CreateAgent(), prompt, CreateContext()));

        Assert.Equal("prompt", error.ParamName);
    }

    [Fact]
    public async Task LaunchAsync_NullAgent_RejectsBeforeUsingDependencies()
    {
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            CreateLauncherForValidation().LaunchAsync(null!, "prompt", CreateContext()));

        Assert.Equal("agent", error.ParamName);
    }

    [Fact]
    public async Task LaunchAsync_NullContext_RejectsBeforeUsingDependencies()
    {
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            CreateLauncherForValidation().LaunchAsync(CreateAgent(), "prompt", null!));

        Assert.Equal("context", error.ParamName);
    }

    private static AgentLauncher CreateLauncherForValidation() => new(sessions: null!, grains: null!);

    private static AgentLaunchContext CreateContext() => new(ProjectId: "project", WorkspacePath: null);

    private static AgentInfo CreateAgent() => new(
        Id: "agent",
        ProjectId: "project",
        Name: "agent",
        Description: "",
        Instructions: "",
        AgentConfig: null,
        Skills: [],
        MaxConcurrentRuns: null,
        Status: AgentStatus.Active,
        CreatedAt: "2026-01-01T00:00:00Z",
        UpdatedAt: "2026-01-01T00:00:00Z");
}
