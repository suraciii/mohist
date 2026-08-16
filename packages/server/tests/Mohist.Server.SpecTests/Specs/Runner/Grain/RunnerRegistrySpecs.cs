using Mohist.Server.Runner.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerRegistrySpecs : WorkflowGrainSpecs
{
    public RunnerRegistrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ListEligibleRunnersAsync_GlobalRunner_IsIncluded()
    {
        var globalRunnerId = $"runner-global-{Guid.NewGuid():N}";
        var globalRunner = Grains.GetGrain<IRunnerGrain>(globalRunnerId);
        await globalRunner.RegisterAsync(new RunnerInfo(globalRunnerId, ["spec/*"], "global-host", null, ["openai/gpt-4"]));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == globalRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_RunnerWithProjectIdField_IsIncluded()
    {
        var projectRunnerId = $"runner-project-{Guid.NewGuid():N}";
        var projectRunner = Grains.GetGrain<IRunnerGrain>(projectRunnerId);
        await projectRunner.RegisterAsync(new RunnerInfo(projectRunnerId, ["spec/*"], "project-host", "test-project", ["openai/gpt-4"]));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == projectRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_RunnerWithOtherProjectIdField_IsStillIncluded()
    {
        var otherProjectRunnerId = $"runner-other-{Guid.NewGuid():N}";
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherProjectRunnerId);
        await otherRunner.RegisterAsync(new RunnerInfo(otherProjectRunnerId, ["spec/*"], "other-host", "other-project", ["openai/gpt-4"]));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == otherProjectRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_MultipleRunners_AreAllReturned()
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

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == globalRunnerId);
        Assert.Contains(eligible, r => r.RunnerId == projectRunnerId);
        Assert.Contains(eligible, r => r.RunnerId == otherProjectRunnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_ReturnsRunnerInfoFields()
    {
        var runnerId = $"runner-info-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registeredAt = TestTime.UtcNow.AddMinutes(-5);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*", "workflow"], "my-host", "test-project", ["openai/gpt-4", "anthropic/claude-3"], "external", registeredAt));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
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
    public async Task ListEligibleRunnersAsync_CalledFromGlobalRegistry_ReturnsAllRunners()
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

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var ids = await registry.ListRunnerIdsAsync();

        Assert.Contains(runnerId, ids);
    }

    [Fact]
    public async Task ListCoderModelsByRuntimeAsync_AggregatesCatalogsPerRuntime()
    {
        var firstRunnerId = $"runner-catalog-a-{Guid.NewGuid():N}";
        var secondRunnerId = $"runner-catalog-b-{Guid.NewGuid():N}";
        var firstRunner = Grains.GetGrain<IRunnerGrain>(firstRunnerId);
        var secondRunner = Grains.GetGrain<IRunnerGrain>(secondRunnerId);
        var firstCatalogs = new Dictionary<string, RuntimeCatalogEntry>
        {
            ["opencode"] = new(["openai/opencode-a"], new Dictionary<string, string[]>
            {
                ["openai/opencode-a"] = ["low"],
            }),
            ["pi"] = new(["anthropic/pi-a"], new Dictionary<string, string[]>
            {
                ["anthropic/pi-a"] = ["medium"],
            }),
        };
        var secondCatalogs = new Dictionary<string, RuntimeCatalogEntry>
        {
            ["opencode"] = new(["openai/opencode-b"], new Dictionary<string, string[]>
            {
                ["openai/opencode-b"] = ["high"],
            }),
            ["pi"] = new(["openai/pi-b"], new Dictionary<string, string[]>
            {
                ["openai/pi-b"] = ["low", "high"],
            }),
        };

        await firstRunner.RegisterAsync(new RunnerInfo(firstRunnerId, ["spec/*"], "catalog-a", null, RuntimeCatalogs: firstCatalogs));
        await secondRunner.RegisterAsync(new RunnerInfo(secondRunnerId, ["spec/*"], "catalog-b", null, RuntimeCatalogs: secondCatalogs));
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);

        try
        {
            var piModels = await registry.ListCoderModelsByRuntimeAsync("pi");
            var opencodeModels = await registry.ListCoderModelsByRuntimeAsync("opencode");
            Assert.Contains("anthropic/pi-a", piModels);
            Assert.Contains("openai/pi-b", piModels);
            Assert.DoesNotContain("openai/opencode-a", piModels);
            Assert.DoesNotContain("openai/opencode-b", piModels);
            Assert.Contains("openai/opencode-a", opencodeModels);
            Assert.Contains("openai/opencode-b", opencodeModels);
            Assert.DoesNotContain("anthropic/pi-a", opencodeModels);
            Assert.DoesNotContain("openai/pi-b", opencodeModels);
            var piVariants = await registry.ListCoderModelVariantsByRuntimeAsync("pi");
            Assert.Equal(["medium"], piVariants["anthropic/pi-a"]);
            Assert.Equal(["low", "high"], piVariants["openai/pi-b"]);
        }
        finally
        {
            await registry.UnregisterAsync(firstRunnerId);
            await registry.UnregisterAsync(secondRunnerId);
        }
    }

    [Fact]
    public async Task RegisterAsync_PreservesRevisionedRuntimeCapabilityMetadata()
    {
        var runnerId = $"runner-capability-revision-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var catalogs = new Dictionary<string, RuntimeCatalogEntry>
        {
            ["pi"] = new(
                Models: ["openai/model"],
                    Variants: new Dictionary<string, string[]>
                    {
                        ["openai/model"] = ["balanced", "high"],
                },
                SupportsReasoningEffort: true,
                Complete: true,
                CapabilityRevision: "catalog-rev-1"),
        };

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "capability-host",
            "test-project",
            RuntimeCatalogs: catalogs));

        var info = await runner.GetInfoAsync();
        var entry = Assert.Single(info!.RuntimeCatalogs!, pair => pair.Key == "pi").Value;
        Assert.NotNull(entry.Models);
        Assert.Equal(["openai/model"], entry.Models);
        Assert.Equal(["balanced", "high"], entry.Variants!["openai/model"]);
        Assert.True(entry.SupportsReasoningEffort);
        Assert.True(entry.Complete);
        Assert.Equal("catalog-rev-1", entry.CapabilityRevision);
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
