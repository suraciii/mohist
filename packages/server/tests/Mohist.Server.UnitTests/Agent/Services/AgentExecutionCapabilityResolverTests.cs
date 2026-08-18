using Mohist.Server.Agent.Services;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentExecutionCapabilityResolverTests
{
    private const string Runtime = "pi";
    private const string Model = "openai/gpt-5";
    private const string Effort = "high";
    private const string Variant = "balanced";

    [Fact]
    public void CompleteCatalogContainingTuple_ReturnsSupportedRunnerAndRevision()
    {
        var result = Resolve(CompleteCatalog());

        Assert.Equal(AgentExecutionCapabilityDisposition.Supported, result.Disposition);
        Assert.Equal("supported", result.DispositionCode);
        Assert.Equal("runner-a", result.RunnerId);
        Assert.Equal("revision-1", result.CapabilityRevision);
        Assert.Null(result.FailureEvidence);
        Assert.Equal(new AgentExecutionCapabilityTuple(Runtime, Model, Effort, Variant), result.FrozenTuple);
    }

    [Fact]
    public void MissingCatalog_ReturnsPendingNeedsSetup()
    {
        var result = Resolve([]);

        Assert.Equal(AgentExecutionCapabilityDisposition.NeedsSetup, result.Disposition);
        Assert.Equal("needs-setup", result.DispositionCode);
        Assert.True(result.IsPending);
        Assert.Equal(new AgentExecutionCapabilityTuple(Runtime, Model, Effort, Variant), result.FrozenTuple);
    }

    [Fact]
    public void AbsentCatalogOnKnownReadyRunner_ReturnsPendingNeedsSetup()
    {
        var result = Resolve([
            new AgentExecutionCapabilitySnapshot(
                "runner-a",
                Runtime,
                Catalog: null)]);

        Assert.Equal(AgentExecutionCapabilityDisposition.NeedsSetup, result.Disposition);
        Assert.True(result.IsPending);
        Assert.Equal(new AgentExecutionCapabilityTuple(Runtime, Model, Effort, Variant), result.FrozenTuple);
    }

    [Fact]
    public void IncompleteCatalog_ReturnsPendingNeedsSetup()
    {
        var result = Resolve([
            new AgentExecutionCapabilitySnapshot(
                "runner-a",
                Runtime,
                CompleteCatalogEntry() with { Complete = false })]);

        Assert.Equal(AgentExecutionCapabilityDisposition.NeedsSetup, result.Disposition);
        Assert.True(result.IsPending);
    }

    [Fact]
    public void RevisionlessCatalog_IsLegacyNonAuthoritative()
    {
        var result = Resolve([
            new AgentExecutionCapabilitySnapshot(
                "runner-a",
                Runtime,
                CompleteCatalogEntry() with { CapabilityRevision = null })]);

        Assert.Equal(AgentExecutionCapabilityDisposition.NeedsSetup, result.Disposition);
        Assert.True(result.IsPending);
    }

    [Fact]
    public void MissingCompleteFlag_IsLegacyNonAuthoritative()
    {
        var result = Resolve([
            new AgentExecutionCapabilitySnapshot(
                "runner-a",
                Runtime,
                CompleteCatalogEntry() with { Complete = null })]);

        Assert.Equal(AgentExecutionCapabilityDisposition.NeedsSetup, result.Disposition);
        Assert.True(result.IsPending);
    }

    [Fact]
    public void BlankCapabilityRevision_IsRevisionless()
    {
        var result = Resolve([
            new AgentExecutionCapabilitySnapshot(
                "runner-a",
                Runtime,
                CompleteCatalogEntry() with { CapabilityRevision = "  " })]);

        Assert.Equal(AgentExecutionCapabilityDisposition.NeedsSetup, result.Disposition);
        Assert.True(result.IsPending);
    }

    [Fact]
    public void KnownRuntimeThatIsNotReady_ReturnsPendingUnavailable()
    {
        var result = Resolve([
            new AgentExecutionCapabilitySnapshot(
                "runner-a",
                Runtime,
                Catalog: null,
                RuntimeReady: false)]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Unavailable, result.Disposition);
        Assert.Equal("runner-a", result.RunnerId);
        Assert.True(result.IsPending);
    }

    [Fact]
    public void NotReadyRuntimeTakesPrecedenceOverCatalogRejection()
    {
        var result = Resolve([
            new AgentExecutionCapabilitySnapshot(
                "runner-a",
                Runtime,
                CompleteCatalogEntry() with
                {
                    SupportsReasoningEffort = false,
                },
                RuntimeReady: false)]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Unavailable, result.Disposition);
        Assert.Equal("runner-a", result.RunnerId);
        Assert.True(result.IsPending);
    }

    [Fact]
    public void EffortOnRuntimeThatExplicitlyDoesNotSupportIt_IsUnsupportedConfiguration()
    {
        var catalog = CompleteCatalogEntry() with
        {
            SupportsReasoningEffort = false,
        };

        var result = Resolve(catalog);

        Assert.Equal(
            AgentExecutionCapabilityDisposition.UnsupportedExecutionConfiguration,
            result.Disposition);
        Assert.Equal("unsupported_execution_configuration", result.DispositionCode);
        Assert.Equal(new AgentExecutionCapabilityTuple(Runtime, Model, Effort, Variant), result.FailureEvidence!.FrozenTuple);
        Assert.Equal("runner-a", result.FailureEvidence.RunnerId);
    }

    [Fact]
    public void CompleteCatalogMissingModel_IsIncompatibleConfiguration()
    {
        var result = Resolve(CompleteCatalogEntry() with { Models = ["another/model"] });

        AssertIncompatible(result);
    }

    [Fact]
    public void CompleteCatalogMissingVariant_IsIncompatibleConfiguration()
    {
        var result = Resolve(CompleteCatalogEntry() with
        {
            Variants = new Dictionary<string, string[]>
            {
                [Model] = ["other"],
            },
        });

        AssertIncompatible(result);
    }

    [Fact]
    public void UnsetEffortDoesNotRequireSupportFromRuntime()
    {
        var catalog = CompleteCatalogEntry() with
        {
            SupportsReasoningEffort = false,
        };
        var result = AgentExecutionCapabilityResolver.Resolve(
            Runtime,
            Model,
            reasoningEffort: null,
            variant: Variant,
            catalogSnapshot: [new AgentExecutionCapabilitySnapshot("runner-a", Runtime, catalog)]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Supported, result.Disposition);
        Assert.Equal("runner-a", result.RunnerId);
    }

    [Fact]
    public void SameTupleAndCatalogAreDeterministicAndDoNotMutateEvidence()
    {
        var catalog = CompleteCatalogEntry();
        IReadOnlyList<AgentExecutionCapabilitySnapshot> snapshot = [
            new AgentExecutionCapabilitySnapshot("runner-a", Runtime, catalog),
        ];
        var beforeModels = catalog.Models!.ToArray();
        var beforeVariants = catalog.Variants![Model].ToArray();

        var first = Resolve(snapshot);
        var second = Resolve(snapshot);

        Assert.Equal(first, second);
        Assert.Equal(beforeModels, catalog.Models);
        Assert.Equal(beforeVariants, catalog.Variants![Model]);
    }

    [Fact]
    public void AuthoritativeCompatibleRunnerWinsOverLegacyPeer()
    {
        var result = AgentExecutionCapabilityResolver.Resolve(
            Runtime,
            Model,
            Effort,
            Variant,
            [
                new AgentExecutionCapabilitySnapshot(
                    "runner-a",
                    Runtime,
                    CompleteCatalogEntry() with { CapabilityRevision = null }),
                new AgentExecutionCapabilitySnapshot("runner-z", Runtime, CompleteCatalogEntry()),
            ]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Supported, result.Disposition);
        Assert.Equal("runner-z", result.RunnerId);
        Assert.Equal("revision-1", result.CapabilityRevision);
    }

    [Fact]
    public void ExplicitRejectionIsDeterministicAndPreservesFrozenTuple()
    {
        var catalog = CompleteCatalogEntry() with
        {
            SupportsReasoningEffort = false,
        };
        IReadOnlyList<AgentExecutionCapabilitySnapshot> snapshot = [
            new AgentExecutionCapabilitySnapshot("runner-a", Runtime, catalog),
        ];

        var first = Resolve(snapshot);
        var second = Resolve(snapshot);

        Assert.Equal(first, second);
        Assert.Equal(
            new AgentExecutionCapabilityTuple(Runtime, Model, Effort, Variant),
            first.FailureEvidence!.FrozenTuple);
    }

    [Fact]
    public void SupportedSelectionIsDeterministicAcrossRunnerOrdering()
    {
        var first = AgentExecutionCapabilityResolver.Resolve(
            Runtime,
            Model,
            Effort,
            Variant,
            [
                new AgentExecutionCapabilitySnapshot("runner-z", Runtime, CompleteCatalogEntry()),
                new AgentExecutionCapabilitySnapshot("runner-a", Runtime, CompleteCatalogEntry()),
            ]);
        var second = AgentExecutionCapabilityResolver.Resolve(
            Runtime,
            Model,
            Effort,
            Variant,
            [
                new AgentExecutionCapabilitySnapshot("runner-a", Runtime, CompleteCatalogEntry()),
                new AgentExecutionCapabilitySnapshot("runner-z", Runtime, CompleteCatalogEntry()),
            ]);

        Assert.Equal(first, second);
        Assert.Equal("runner-a", first.RunnerId);
    }

    [Fact]
    public void UnsetModelOnCompleteCatalog_IsSupported()
    {
        // No model supplied means the runtime uses its default. The
        // catalog is the configuration source, not the execution
        // authority, so an unset model is not a compatibility
        // violation.
        var result = AgentExecutionCapabilityResolver.Resolve(
            Runtime,
            model: null,
            reasoningEffort: null,
            variant: null,
            catalogSnapshot: [new AgentExecutionCapabilitySnapshot("runner-a", Runtime, CompleteCatalogEntry())]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Supported, result.Disposition);
        Assert.Equal("runner-a", result.RunnerId);
    }

    [Fact]
    public void UnsetModelOnEffortUnsupportedRuntime_IsSupported()
    {
        // An unset model is allowed even when the runtime opts out of
        // effort — the effort exemption is the same one that lets
        // `UnsetEffortDoesNotRequireSupportFromRuntime` pass.
        var catalog = CompleteCatalogEntry() with
        {
            SupportsReasoningEffort = false,
        };
        var result = AgentExecutionCapabilityResolver.Resolve(
            Runtime,
            model: null,
            reasoningEffort: null,
            variant: null,
            catalogSnapshot: [new AgentExecutionCapabilitySnapshot("runner-a", Runtime, catalog)]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Supported, result.Disposition);
    }

    [Fact]
    public void OpenCodeProductionShape_AcceptsAnyModelWithoutDiscovery()
    {
        // The shipped OpenCode catalog reports the explicit effort
        // contract (supportsReasoningEffort=false) but has no model
        // discovery yet. The empty model list is the "no discovery"
        // signal: the runtime validates any supplied model at
        // execution time, so any model name is admitted.
        var catalog = new RuntimeCatalogEntry(
            Models: [],
            Variants: [],
            SupportsReasoningEffort: false,
            Complete: true,
            CapabilityRevision: "test-opencode-no-discovery-v1");

        var result = AgentExecutionCapabilityResolver.Resolve(
            "opencode",
            "anthropic/claude-sonnet-4-20250514",
            null,
            null,
            [new AgentExecutionCapabilitySnapshot("runner-a", "opencode", catalog)]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Supported, result.Disposition);
        Assert.Equal("test-opencode-no-discovery-v1", result.CapabilityRevision);
    }

    [Fact]
    public void OpenCodeProductionShape_RejectsExplicitEffortDeterministically()
    {
        // The shipped OpenCode catalog reports the explicit effort
        // contract (supportsReasoningEffort=false). An explicit effort
        // on OpenCode is a deterministic unsupported_execution_configuration
        // preflight failure — never a silent drop.
        var catalog = new RuntimeCatalogEntry(
            Models: [],
            Variants: [],
            SupportsReasoningEffort: false,
            Complete: true,
            CapabilityRevision: "test-opencode-no-discovery-v1");

        var result = AgentExecutionCapabilityResolver.Resolve(
            "opencode",
            "anthropic/claude-sonnet-4-20250514",
            "high",
            null,
            [new AgentExecutionCapabilitySnapshot("runner-a", "opencode", catalog)]);

        Assert.Equal(
            AgentExecutionCapabilityDisposition.UnsupportedExecutionConfiguration,
            result.Disposition);
        Assert.Equal("unsupported_execution_configuration", result.DispositionCode);
        Assert.Equal(
            new AgentExecutionCapabilityTuple("opencode", "anthropic/claude-sonnet-4-20250514", "high", null),
            result.FailureEvidence!.FrozenTuple);
        Assert.Equal("test-opencode-no-discovery-v1", result.FailureEvidence.CapabilityRevision);
    }

    [Fact]
    public void OpenCodeProductionShape_RejectsExplicitEffortEvenWithoutModel()
    {
        // Effort rejection takes precedence over a missing model
        // (the OpenCode design contract is "supportsReasoningEffort=
        // false", so any explicit effort is rejected before the
        // model check runs).
        var catalog = new RuntimeCatalogEntry(
            Models: [],
            Variants: [],
            SupportsReasoningEffort: false,
            Complete: true,
            CapabilityRevision: "test-opencode-no-discovery-v1");

        var result = AgentExecutionCapabilityResolver.Resolve(
            "opencode",
            null,
            "high",
            null,
            [new AgentExecutionCapabilitySnapshot("runner-a", "opencode", catalog)]);

        Assert.Equal(
            AgentExecutionCapabilityDisposition.UnsupportedExecutionConfiguration,
            result.Disposition);
    }

    [Fact]
    public void OpenCodeProductionShape_AcceptsExplicitVariant()
    {
        // The shipped OpenCode catalog has no variant discovery yet;
        // an empty variant map means the runtime validates the
        // variant at execution time, so any variant name is admitted.
        var catalog = new RuntimeCatalogEntry(
            Models: [],
            Variants: [],
            SupportsReasoningEffort: false,
            Complete: true,
            CapabilityRevision: "test-opencode-no-discovery-v1");

        var result = AgentExecutionCapabilityResolver.Resolve(
            "opencode",
            "anthropic/claude-sonnet-4-20250514",
            null,
            "balanced",
            [new AgentExecutionCapabilitySnapshot("runner-a", "opencode", catalog)]);

        Assert.Equal(AgentExecutionCapabilityDisposition.Supported, result.Disposition);
    }

    [Fact]
    public void AuthoritativeCatalogWithExplicitModelRejectsUnknownModel()
    {
        // A catalog that DOES list explicit models still rejects an
        // unknown model as a deterministic incompatibility — the
        // "no discovery" carve-out only applies to empty model
        // lists.
        var catalog = CompleteCatalogEntry();
        var result = AgentExecutionCapabilityResolver.Resolve(
            Runtime,
            model: "unknown/model",
            reasoningEffort: null,
            variant: null,
            catalogSnapshot: [new AgentExecutionCapabilitySnapshot("runner-a", Runtime, catalog)]);

        Assert.Equal(
            AgentExecutionCapabilityDisposition.IncompatibleExecutionConfiguration,
            result.Disposition);
    }

    private static AgentExecutionCapabilityResolution Resolve(RuntimeCatalogEntry catalog) =>
        Resolve([
            new AgentExecutionCapabilitySnapshot("runner-a", Runtime, catalog),
        ]);

    private static AgentExecutionCapabilityResolution Resolve(
        IReadOnlyList<AgentExecutionCapabilitySnapshot> snapshot) =>
        AgentExecutionCapabilityResolver.Resolve(Runtime, Model, Effort, Variant, snapshot);

    private static RuntimeCatalogEntry CompleteCatalog() => CompleteCatalogEntry();

    private static RuntimeCatalogEntry CompleteCatalogEntry() => new(
        Models: [Model],
        Variants: new Dictionary<string, string[]>
        {
            [Model] = [Variant],
        },
        SupportsReasoningEffort: true,
        Complete: true,
        CapabilityRevision: "revision-1");

    private static void AssertIncompatible(AgentExecutionCapabilityResolution result)
    {
        Assert.Equal(
            AgentExecutionCapabilityDisposition.IncompatibleExecutionConfiguration,
            result.Disposition);
        Assert.Equal("incompatible_execution_configuration", result.DispositionCode);
        Assert.Equal(new AgentExecutionCapabilityTuple(Runtime, Model, Effort, Variant), result.FailureEvidence!.FrozenTuple);
        Assert.Equal("runner-a", result.FailureEvidence.RunnerId);
        Assert.False(result.IsPending);
    }
}
