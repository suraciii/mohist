using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Runner;

public sealed class RunnerCapabilityGateTests
{
    [Fact]
    public void OpenCodeClaim_AcceptsExplicitModelWhenCatalogIsEmpty()
    {
        var info = Runner("opencode", new RuntimeCatalogEntry([], [], SupportsReasoningEffort: false));

        Assert.True(RunnerCapabilityGate.Matches(
            info,
            null,
            new Dictionary<string, RuntimeReadinessWitness>(),
            Expectation("opencode", "openai/gpt-5.6-sol")));
    }

    [Fact]
    public void OpenCodeClaim_AcceptsExplicitModelOutsideDiscoveredCatalog()
    {
        var info = Runner("opencode", new RuntimeCatalogEntry(["openai/discovered"], [], SupportsReasoningEffort: false));

        Assert.True(RunnerCapabilityGate.Matches(
            info,
            null,
            new Dictionary<string, RuntimeReadinessWitness>(),
            Expectation("opencode", "openai/operator-configured")));
    }

    [Fact]
    public void PiClaim_RejectsExplicitModelOutsideAuthoritativeCatalog()
    {
        var info = Runner("pi", new RuntimeCatalogEntry(["openai/discovered"], [], SupportsReasoningEffort: true));

        Assert.False(RunnerCapabilityGate.Matches(
            info,
            null,
            new Dictionary<string, RuntimeReadinessWitness>(),
            Expectation("pi", "openai/unknown")));
    }

    private static RunnerInfo Runner(string runtime, RuntimeCatalogEntry catalog) => new(
        "runner-a",
        [],
        "test-host",
        null,
        RuntimeCatalogs: new Dictionary<string, RuntimeCatalogEntry> { [runtime] = catalog });

    private static CapabilityClaimExpectation Expectation(string runtime, string model) => new(
        WorkDispatchOwnerKinds.AgentJob,
        "job-a",
        "work-a",
        runtime,
        model,
        null,
        null,
        null,
        null,
        null,
        []);
}
