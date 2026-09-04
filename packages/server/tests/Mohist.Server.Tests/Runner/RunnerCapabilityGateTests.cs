using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.Tests.Runner;

[Trait("level", "L0")]
public sealed class RunnerCapabilityGateTests
{
    private const string ConnectionGeneration = "connection-1";

    [Fact]
    public void OpenCodeClaim_AcceptsExplicitModelWhenCatalogIsEmpty()
    {
        var info = Runner("opencode", new RuntimeCatalogEntry([], [], SupportsReasoningEffort: false));

        Assert.True(Matches(info, Expectation("opencode", "openai/gpt-5.6-sol")));
    }

    [Fact]
    public void OpenCodeClaim_AcceptsExplicitModelOutsideDiscoveredCatalog()
    {
        var info = Runner("opencode", new RuntimeCatalogEntry(["openai/discovered"], [], SupportsReasoningEffort: false));

        Assert.True(Matches(info, Expectation("opencode", "openai/operator-configured")));
    }

    [Fact]
    public void PiClaim_RejectsExplicitModelOutsideAuthoritativeCatalog()
    {
        var info = Runner("pi", new RuntimeCatalogEntry(["openai/discovered"], [], SupportsReasoningEffort: true));

        Assert.False(Matches(info, Expectation("pi", "openai/unknown")));
    }

    [Theory]
    [InlineData("spec/action", true)]
    [InlineData("manager-redaction-v1", false)]
    [InlineData("execution-source-v1", false)]
    public void SpecWildcard_OnlyMatchesSpecNamespace(string requiredCapability, bool expected)
    {
        var info = Runner(
            "pi",
            new RuntimeCatalogEntry(["openai/model"], [], SupportsReasoningEffort: false),
            ["spec/*"]);

        Assert.Equal(expected, Matches(
            info,
            Expectation("pi", "openai/model") with { RequiredCapabilities = [requiredCapability] }));
    }

    [Theory]
    [InlineData(true, 1, ConnectionGeneration, true)]
    [InlineData(false, 1, ConnectionGeneration, false)]
    [InlineData(true, 2, ConnectionGeneration, false)]
    [InlineData(true, 1, "connection-2", false)]
    public void ClaimWithoutReasoningEffort_RequiresMatchingReadinessWitness(
        bool ready,
        long generation,
        string readinessConnectionGeneration,
        bool expected)
    {
        var info = Runner("pi", new RuntimeCatalogEntry(["openai/model"], [], SupportsReasoningEffort: false));
        var readiness = new Dictionary<string, RuntimeReadinessWitness>
        {
            ["pi"] = new("pi", ready, generation),
        };

        Assert.Equal(expected, RunnerCapabilityGate.Matches(
            info,
            readinessConnectionGeneration,
            readiness,
            Expectation("pi", "openai/model")));
    }

    private static bool Matches(RunnerInfo info, CapabilityClaimExpectation expectation) =>
        RunnerCapabilityGate.Matches(
            info,
            ConnectionGeneration,
            new Dictionary<string, RuntimeReadinessWitness>
            {
                [expectation.Runtime!] = new(expectation.Runtime!, Ready: true, Generation: 1),
            },
            expectation);

    private static RunnerInfo Runner(
        string runtime,
        RuntimeCatalogEntry catalog,
        string[]? capabilities = null) => new(
        "runner-a",
        capabilities ?? [],
        "test-host",
        null,
        ConnectionGeneration: ConnectionGeneration,
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
        1,
        ConnectionGeneration,
        []);
}
