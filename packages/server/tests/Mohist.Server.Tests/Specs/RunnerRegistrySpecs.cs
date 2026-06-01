using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RunnerRegistrySpecs : WorkflowGrainSpecs
{
    public RunnerRegistrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ListEligibleRunnersAsync_GlobalRunner_ReturnsForSelectedProject()
    {
        var globalRunnerId = $"runner-global-{Guid.NewGuid():N}";
        var globalRunner = Grains.GetGrain<IRunnerGrain>(globalRunnerId);
        await globalRunner.RegisterAsync(new RunnerInfo(globalRunnerId, ["spec/*"], "global-host", null, ["openai/gpt-4"]));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry("test-project"));
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == globalRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_ProjectRunner_ReturnsForSameProject()
    {
        var projectRunnerId = $"runner-project-{Guid.NewGuid():N}";
        var projectRunner = Grains.GetGrain<IRunnerGrain>(projectRunnerId);
        await projectRunner.RegisterAsync(new RunnerInfo(projectRunnerId, ["spec/*"], "project-host", "test-project", ["openai/gpt-4"]));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry("test-project"));
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == projectRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_OtherProjectRunner_IsExcluded()
    {
        var otherProjectRunnerId = $"runner-other-{Guid.NewGuid():N}";
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherProjectRunnerId);
        await otherRunner.RegisterAsync(new RunnerInfo(otherProjectRunnerId, ["spec/*"], "other-host", "other-project", ["openai/gpt-4"]));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry("test-project"));
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.DoesNotContain(eligible, r => r.RunnerId == otherProjectRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_CombinesGlobalAndProjectRunners()
    {
        var globalRunnerId = $"runner-g-{Guid.NewGuid():N}";
        var projectRunnerId = $"runner-p-{Guid.NewGuid():N}";
        var otherProjectRunnerId = $"runner-o-{Guid.NewGuid():N}";

        var globalRunner = Grains.GetGrain<IRunnerGrain>(globalRunnerId);
        await globalRunner.RegisterAsync(new RunnerInfo(globalRunnerId, ["spec/*"], "global-host", null));

        var projectRunner = Grains.GetGrain<IRunnerGrain>(projectRunnerId);
        await projectRunner.RegisterAsync(new RunnerInfo(projectRunnerId, ["spec/*"], "project-host", "test-project"));

        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherProjectRunnerId);
        await otherRunner.RegisterAsync(new RunnerInfo(otherProjectRunnerId, ["spec/*"], "other-host", "other-project"));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry("test-project"));
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == globalRunnerId);
        Assert.Contains(eligible, r => r.RunnerId == projectRunnerId);
        Assert.DoesNotContain(eligible, r => r.RunnerId == otherProjectRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_ReturnsRunnerInfoFields()
    {
        var runnerId = $"runner-info-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registeredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", "workflow"], "my-host", "test-project", ["openai/gpt-4", "anthropic/claude-3"], "external", registeredAt));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry("test-project"));
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        var info = Assert.Single(eligible, r => r.RunnerId == runnerId);
        Assert.Equal(runnerId, info.RunnerId);
        Assert.Equal("external", info.Kind);
        Assert.Equal("my-host", info.Hostname);
        Assert.Equal("test-project", info.ProjectId);
        Assert.Equal(new[] { "spec/*", "workflow" }, info.Capabilities);
        Assert.Equal(new[] { "openai/gpt-4", "anthropic/claude-3" }, info.CoderModels);
        Assert.NotNull(info.RegisteredAt);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_CalledFromGlobalRegistry_ReturnsGlobalAndProjectRunners()
    {
        var globalRunnerId = $"runner-g2-{Guid.NewGuid():N}";
        var projectRunnerId = $"runner-p2-{Guid.NewGuid():N}";

        var globalRunner = Grains.GetGrain<IRunnerGrain>(globalRunnerId);
        await globalRunner.RegisterAsync(new RunnerInfo(globalRunnerId, ["spec/*"], "global-host", null));

        var projectRunner = Grains.GetGrain<IRunnerGrain>(projectRunnerId);
        await projectRunner.RegisterAsync(new RunnerInfo(projectRunnerId, ["spec/*"], "project-host", "test-project"));

        var globalRegistry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await globalRegistry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == globalRunnerId);
        Assert.Contains(eligible, r => r.RunnerId == projectRunnerId);
    }

    [Fact]
    public async Task ListRunnerIdsAsync_RemainsCompatible()
    {
        var runnerId = $"runner-compat-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "compat-host", "test-project"));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry("test-project"));
        var ids = await registry.ListRunnerIdsAsync();

        Assert.Contains(runnerId, ids);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_DeduplicatesByRunnerId()
    {
        var runnerId = $"runner-dup-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "dup-host", null));

        var globalRegistry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await globalRegistry.ListEligibleRunnersAsync("test-project");

        Assert.Single(eligible, r => r.RunnerId == runnerId);
    }
}
