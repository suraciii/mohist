using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Runner.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Trait("level", "L0")]
public sealed class RunnerRegistrySpecs
{
    [Fact]
    public async Task ListEligibleRunnersAsync_GlobalRunner_IsIncluded()
    {
        var harness = new RunnerRegistryHarness();
        var runnerId = $"runner-global-{Guid.NewGuid():N}";
        await harness.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "global-host", null, ["openai/gpt-4"]));

        var eligible = await harness.Registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == runnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_RunnerWithProjectIdField_IsIncluded()
    {
        var harness = new RunnerRegistryHarness();
        var runnerId = $"runner-project-{Guid.NewGuid():N}";
        await harness.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "project-host", "test-project", ["openai/gpt-4"]));

        var eligible = await harness.Registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == runnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_RunnerWithOtherProjectIdField_IsStillIncluded()
    {
        var harness = new RunnerRegistryHarness();
        var runnerId = $"runner-other-{Guid.NewGuid():N}";
        await harness.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "other-host", "other-project", ["openai/gpt-4"]));

        var eligible = await harness.Registry.ListEligibleRunnersAsync("test-project");

        Assert.Contains(eligible, r => r.RunnerId == runnerId);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_MultipleRunners_AreAllReturned()
    {
        var harness = new RunnerRegistryHarness();
        var runners = new[]
        {
            new RunnerInfo($"runner-g-{Guid.NewGuid():N}", ["spec/*"], "global-host", null),
            new RunnerInfo($"runner-p-{Guid.NewGuid():N}", ["spec/*"], "project-host", "test-project"),
            new RunnerInfo($"runner-o-{Guid.NewGuid():N}", ["spec/*"], "other-host", "other-project"),
        };
        foreach (var runner in runners)
            await harness.RegisterAsync(runner);

        var eligible = await harness.Registry.ListEligibleRunnersAsync("test-project");

        Assert.All(runners, runner => Assert.Contains(eligible, r => r.RunnerId == runner.RunnerId));
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_ReturnsRunnerInfoFields()
    {
        var harness = new RunnerRegistryHarness();
        var runnerId = $"runner-info-{Guid.NewGuid():N}";
        var registeredAt = harness.TimeProvider.GetUtcNow().AddMinutes(-5);
        var expected = new RunnerInfo(
            runnerId,
            ["spec/*", "workflow"],
            "my-host",
            "test-project",
            ["openai/gpt-4", "anthropic/claude-3"],
            "external",
            registeredAt);
        await harness.RegisterAsync(expected);

        var eligible = await harness.Registry.ListEligibleRunnersAsync("test-project");

        var info = Assert.Single(eligible, r => r.RunnerId == runnerId);
        Assert.Equal(expected, info);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_CalledFromGlobalRegistry_ReturnsAllRunners()
    {
        var harness = new RunnerRegistryHarness();
        var globalRunner = new RunnerInfo($"runner-g2-{Guid.NewGuid():N}", ["spec/*"], "global-host", null);
        var projectRunner = new RunnerInfo($"runner-p2-{Guid.NewGuid():N}", ["spec/*"], "project-host", "test-project");
        await harness.RegisterAsync(globalRunner);
        await harness.RegisterAsync(projectRunner);

        var eligible = await harness.Registry.ListEligibleRunnersAsync("test-project");

        Assert.Equal(2, eligible.Count);
        Assert.Contains(eligible, r => r.RunnerId == globalRunner.RunnerId);
        Assert.Contains(eligible, r => r.RunnerId == projectRunner.RunnerId);
    }

    [Fact]
    public async Task ListRunnerIdsAsync_RemainsCompatible()
    {
        var harness = new RunnerRegistryHarness();
        var runnerId = $"runner-compat-{Guid.NewGuid():N}";
        await harness.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "compat-host", "test-project"));

        var ids = await harness.Registry.ListRunnerIdsAsync();

        Assert.Contains(runnerId, ids);
    }

    [Fact]
    public async Task ListCoderModelsByRuntimeAsync_AggregatesCatalogsPerRuntime()
    {
        var harness = new RunnerRegistryHarness();
        var firstCatalogs = new Dictionary<string, RuntimeCatalogEntry>
        {
            ["opencode"] = new(["openai/opencode-a"], new Dictionary<string, string[]>
            {
                ["openai/opencode-a"] = ["low"],
            }),
            ["pi"] = new(["anthropic/pi-a"], new Dictionary<string, string[]>
            {
                ["anthropic/pi-a"] = ["medium"],
            }, ReasoningEfforts: new Dictionary<string, string[]>
            {
                ["anthropic/pi-a"] = ["low", "high"],
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
        await harness.RegisterAsync(new RunnerInfo($"runner-catalog-a-{Guid.NewGuid():N}", ["spec/*"], "catalog-a", null, RuntimeCatalogs: firstCatalogs));
        await harness.RegisterAsync(new RunnerInfo($"runner-catalog-b-{Guid.NewGuid():N}", ["spec/*"], "catalog-b", null, RuntimeCatalogs: secondCatalogs));

        var piModels = await harness.Registry.ListCoderModelsByRuntimeAsync("pi");
        var opencodeModels = await harness.Registry.ListCoderModelsByRuntimeAsync("opencode");
        var piVariants = await harness.Registry.ListCoderModelVariantsByRuntimeAsync("pi");
        var piReasoningEfforts = await harness.Registry.ListCoderReasoningEffortsByRuntimeAsync("pi");

        Assert.Equal(["anthropic/pi-a", "openai/pi-b"], piModels);
        Assert.Equal(["openai/opencode-a", "openai/opencode-b"], opencodeModels);
        Assert.Equal(["medium"], piVariants["anthropic/pi-a"]);
        Assert.Equal(["low", "high"], piVariants["openai/pi-b"]);
        Assert.Equal(["low", "high"], piReasoningEfforts["anthropic/pi-a"]);
    }

    [Fact]
    public async Task RegisterAsync_PreservesRevisionedRuntimeCapabilityMetadata()
    {
        var harness = new RunnerRegistryHarness();
        var runnerId = $"runner-capability-revision-{Guid.NewGuid():N}";
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
        await harness.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "capability-host", "test-project", RuntimeCatalogs: catalogs));

        var info = Assert.Single(await harness.Registry.ListRunnersAsync(), r => r.RunnerId == runnerId);
        var entry = Assert.Single(info.RuntimeCatalogs!, pair => pair.Key == "pi").Value;

        Assert.NotNull(entry.Models);
        Assert.Equal(["openai/model"], entry.Models!);
        Assert.Equal(["balanced", "high"], entry.Variants!["openai/model"]);
        Assert.True(entry.SupportsReasoningEffort);
        Assert.True(entry.Complete);
        Assert.Equal("catalog-rev-1", entry.CapabilityRevision);
    }

    [Fact]
    public async Task ListEligibleRunnersAsync_DeduplicatesByRunnerId()
    {
        var harness = new RunnerRegistryHarness();
        var runnerId = $"runner-dup-{Guid.NewGuid():N}";
        await harness.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "dup-host", null));

        var eligible = await harness.Registry.ListEligibleRunnersAsync("test-project");

        Assert.Single(eligible, r => r.RunnerId == runnerId);
    }

    private sealed class RunnerRegistryHarness
    {
        public FakeTimeProvider TimeProvider { get; } = new(DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        public RunnerRegistryGrain Registry { get; }

        private readonly GrainFactoryProxy _factory;

        public RunnerRegistryHarness()
        {
            var factory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
            _factory = (GrainFactoryProxy)(object)factory;
            Registry = new RunnerRegistryGrain(
                NullLogger<RunnerRegistryGrain>.Instance,
                TimeProvider,
                factory);
            _factory.Registry = Registry;
        }

        public async Task RegisterAsync(RunnerInfo info)
        {
            var runner = DispatchProxy.Create<IRunnerGrain, ActiveRunnerProxy>();
            _factory.Runners[info.RunnerId] = (IRunnerGrain)(object)runner;
            await Registry.RegisterAsync(info);
        }
    }

    private class ActiveRunnerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IRunnerGrain.IsPresenceLeaseActiveAsync)
                ? Task.FromResult(true)
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public RunnerRegistryGrain Registry { get; set; } = null!;
        public Dictionary<string, IRunnerGrain> Runners { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod)
            {
                var type = targetMethod.GetGenericArguments()[0];
                if (type == typeof(IRunnerRegistryGrain))
                    return Registry;
                if (type == typeof(IRunnerGrain) && args is { Length: > 0 } && args[0] is string runnerId)
                    return Runners[runnerId];
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
