using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Runner;

public sealed class RuntimeCatalogEntryContractTests
{
    [Fact]
    public void LegacyEntry_DeserializesWithCapabilityFieldsAbsent()
    {
        var entry = JSON.DeserializeOrThrow<RuntimeCatalogEntry>(
            "{\"models\":[\"openai/gpt-5\"],\"variants\":{\"openai/gpt-5\":[\"balanced\"]}}");

        Assert.Equal(["openai/gpt-5"], entry.Models!);
        Assert.Equal(["balanced"], entry.Variants!["openai/gpt-5"]);
        Assert.Null(entry.SupportsReasoningEffort);
        Assert.Null(entry.Complete);
        Assert.Null(entry.CapabilityRevision);
        Assert.DoesNotContain("capabilityRevision", JSON.Serialize(entry), StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityFields_RoundTripWithoutChangingTheirValues()
    {
        var original = new RuntimeCatalogEntry(
            ["openai/gpt-5"],
            new Dictionary<string, string[]> { ["openai/gpt-5"] = ["balanced"] },
            SupportsReasoningEffort: true,
            Complete: true,
            CapabilityRevision: "revision-a");

        var roundTripped = JSON.DeserializeOrThrow<RuntimeCatalogEntry>(JSON.Serialize(original));

        Assert.Equal(original.Models, roundTripped.Models);
        Assert.Equal(original.Variants, roundTripped.Variants);
        Assert.Equal(original.SupportsReasoningEffort, roundTripped.SupportsReasoningEffort);
        Assert.Equal(original.Complete, roundTripped.Complete);
        Assert.Equal(original.CapabilityRevision, roundTripped.CapabilityRevision);
    }
}
