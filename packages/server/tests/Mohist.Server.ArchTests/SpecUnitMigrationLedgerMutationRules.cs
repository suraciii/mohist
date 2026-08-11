using Xunit;

namespace Mohist.Server.ArchTests;

public sealed partial class SpecUnitMigrationLedgerRules
{
    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_SourceBodyMutationChangesClosureBinding()
    {
        var source = BoundedSource("public int Value() => 1;");
        using var productionInventory = CreateBoundedLiveInventory(source);
        var before = CurrentClassification(productionInventory, BoundedProofFqn);
        var row = BoundCurrentRow(before);
        Assert.Empty(SpecUnitMigrationLedgerValidator.ValidateCurrentRowForTests(row, before, productionInventory));

        using var inventory = productionInventory.WithSourceContent(BoundedProofPath,
            source.Replace("=> 1", "=> 2", StringComparison.Ordinal));
        var classification = CurrentClassification(inventory, BoundedProofFqn);

        Assert.Equal(productionInventory.SourceTree.FileCount, inventory.SourceTree.FileCount);
        Assert.NotEqual(productionInventory.SourceTree.Digest, inventory.SourceTree.Digest);
        Assert.NotEqual(productionInventory.DiscoveryBindingIdentity, inventory.DiscoveryBindingIdentity);
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateCurrentRowForTests(row, classification, inventory), violation =>
            violation.Contains("source-content digest mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_MethodBodyMutationRebindsDiscoverySnapshot()
    {
        var source = BoundedSource("public int Value() => 1;");
        using var inventory = CreateBoundedLiveInventory(source);
        var before = CurrentClassification(inventory, BoundedProofFqn);

        using var mutated = inventory.WithSourceContent(BoundedProofPath, source.Replace(
            "public void CompiledCase() { }",
            "public void CompiledCase() { var value = 2; Assert.Equal(2, value); }",
            StringComparison.Ordinal));
        var after = CurrentClassification(mutated, BoundedProofFqn);

        Assert.Equal(before.ExecutableCaseIdentityDigest, after.ExecutableCaseIdentityDigest);
        Assert.NotEqual(inventory.DiscoveryBindingIdentity, mutated.DiscoveryBindingIdentity);
        Assert.NotEqual(before.SourceContentDigest, after.SourceContentDigest);
        Assert.NotEqual(before.ClosureIdentityDigest, after.ClosureIdentityDigest);
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_SemanticErrorMutationFailsClosed()
    {
        var source = BoundedSource("public int Value() => 1;");
        using var inventory = CreateBoundedLiveInventory(source);
        using var mutated = inventory.WithSourceContent(BoundedProofPath, source.Replace(
            "public void CompiledCase() { }",
            "public void CompiledCase() { int value = \"wrong\"; _ = value; }",
            StringComparison.Ordinal));

        var classification = CurrentClassification(mutated, BoundedProofFqn);

        Assert.Contains(mutated.Diagnostics, diagnostic => diagnostic.StartsWith("SEMANTIC|", StringComparison.Ordinal)
            && diagnostic.Contains("Cannot implicitly convert", StringComparison.Ordinal));
        Assert.Contains(classification.Blockers, blocker => blocker.Contains("source diagnostics: SEMANTIC|", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_BaseTypeMutationFailsClosed()
    {
        var source = BoundedSource("public int Value() => 1;");
        using var inventory = CreateBoundedLiveInventory(source);
        using var mutated = inventory.WithSourceContent(BoundedProofPath, source.Replace(
            "public sealed class BoundedLedgerSpecs",
            "public sealed class BoundedLedgerSpecs : MissingFixture",
            StringComparison.Ordinal));

        var classification = CurrentClassification(mutated, BoundedProofFqn);

        Assert.Contains(classification.Blockers, blocker => blocker.Contains("fixture/spec base MissingFixture", StringComparison.Ordinal));
        Assert.Contains(mutated.Diagnostics, diagnostic => diagnostic.Contains("MissingFixture", StringComparison.Ordinal));
        Assert.NotEqual(inventory.DiscoveryBindingIdentity, mutated.DiscoveryBindingIdentity);
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_ClassAttributeMutationFailsClosed()
    {
        var source = BoundedSource("public int Value() => 1;");
        using var inventory = CreateBoundedLiveInventory(source);
        using var mutated = inventory.WithSourceContent(BoundedProofPath, source.Replace(
            "public sealed class BoundedLedgerSpecs",
            "[Collection(\"bounded\")]\n        public sealed class BoundedLedgerSpecs",
            StringComparison.Ordinal));

        var classification = CurrentClassification(mutated, BoundedProofFqn);

        Assert.Contains(classification.Blockers, blocker => blocker.Contains("collection fixture attribute", StringComparison.Ordinal));
        Assert.NotEqual(inventory.DiscoveryBindingIdentity, mutated.DiscoveryBindingIdentity);
        Assert.NotEqual(inventory.SourceTree.Digest, mutated.SourceTree.Digest);
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_ExternalBoundaryMutationFailsClosed()
    {
        var source = BoundedSource("public int Value() => 1;");
        using var inventory = CreateBoundedLiveInventory(source);
        using var mutated = inventory.WithSourceContent(BoundedProofPath, source.Replace(
            "public void CompiledCase() { }",
            "public void CompiledCase() { _ = new HttpClient(); }",
            StringComparison.Ordinal));

        var classification = CurrentClassification(mutated, BoundedProofFqn);

        Assert.Contains(classification.Blockers, blocker => blocker.Contains("external boundary symbol HttpClient", StringComparison.Ordinal));
        Assert.DoesNotContain(mutated.Diagnostics, diagnostic => diagnostic.Contains("HttpClient", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_HelperMutationRebindsReverseDependencyClosure()
    {
        const string specFqn = "Mohist.Server.SpecTests.Specs.Negative.HelperConsumerSpecs";
        const string helperPath = "Mohist.Server.SpecTests/Specs/Negative/ClosureHelper.cs";
        const string originalHelper = """
            namespace Mohist.Server.SpecTests.Specs.Negative;
            public static class ClosureHelper { public static int Value() => 1; }
            """;
        using var inventory = SpecUnitMigrationInventory.Create(
        [
            Source(helperPath, originalHelper),
            Source("Mohist.Server.SpecTests/Specs/Negative/HelperConsumerSpecs.cs", """
                using Xunit;
                namespace Mohist.Server.SpecTests.Specs.Negative;
                public class HelperConsumerSpecs { [Fact] public void UsesHelper() => Assert.Equal(1, ClosureHelper.Value()); }
                """),
        ]).BindSourceDiscovery();
        var before = CurrentClassification(inventory, specFqn);

        using var mutated = inventory.WithSourceContent(helperPath,
            originalHelper.Replace("=> 1", "=> 2", StringComparison.Ordinal));
        var after = CurrentClassification(mutated, specFqn);

        Assert.Contains("Mohist.Server.SpecTests.Specs.Negative.ClosureHelper", after.Closure);
        Assert.NotEqual(inventory.DiscoveryBindingIdentity, mutated.DiscoveryBindingIdentity);
        Assert.NotEqual(before.SourceContentDigest, after.SourceContentDigest);
        Assert.NotEqual(before.ClosureIdentityDigest, after.ClosureIdentityDigest);
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_DiscoveryMutationRebuildsCompiledFacts()
    {
        const string fqn = "Mohist.Server.SpecTests.Specs.Negative.DiscoveryShapeSpecs";
        const string path = "Mohist.Server.SpecTests/Specs/Negative/DiscoveryShapeSpecs.cs";
        const string source = """
            using Xunit;
            namespace Mohist.Server.SpecTests.Specs.Negative;
            public class DiscoveryShapeSpecs { [Fact] public void Case() { } }
            """;
        using var inventory = SpecUnitMigrationInventory.Create([Source(path, source)]).BindSourceDiscovery();
        var before = CurrentClassification(inventory, fqn);
        using var mutated = inventory.WithSourceContent(path, """
            using Xunit;
            namespace Mohist.Server.SpecTests.Specs.Negative;
            public class DiscoveryShapeSpecs { [Theory] [InlineData(1)] public void Case(int value) { } }
            """);

        var after = CurrentClassification(mutated, fqn);

        Assert.Equal((1, 0, 0, 1), (before.FactMethods, before.TheoryMethods, before.InlineDataRows, before.ExecutableCaseCount));
        Assert.Equal((0, 1, 1, 1), (after.FactMethods, after.TheoryMethods, after.InlineDataRows, after.ExecutableCaseCount));
        Assert.NotEqual(before.ExecutableCaseIdentityDigest, after.ExecutableCaseIdentityDigest);
        Assert.NotEqual(inventory.DiscoveryBindingIdentity, mutated.DiscoveryBindingIdentity);
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_IncompleteStaticDiscoveryFailsClosed()
    {
        const string fqn = "Mohist.Server.SpecTests.Specs.Negative.IncompleteDiscoverySpecs";
        const string path = "Mohist.Server.SpecTests/Specs/Negative/IncompleteDiscoverySpecs.cs";
        using var inventory = SpecUnitMigrationInventory.Create([Source(path, """
            using Xunit;
            namespace Mohist.Server.SpecTests.Specs.Negative;
            public class IncompleteDiscoverySpecs { [Theory] public void Case(int value) { } }
            """)]).BindSourceDiscovery();

        var classification = CurrentClassification(inventory, fqn);

        Assert.Equal(0, classification.ExecutableCaseCount);
        Assert.Contains(classification.Blockers, blocker => blocker.Contains("compiled MTP discovery unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_SnapshotCachesAndReferencesAreReclaimed()
    {
        var source = BoundedSource("public int Value() => 1;");
        using var inventory = CreateBoundedLiveInventory(source);
        _ = CurrentClassification(inventory, BoundedProofFqn);
        var metadataCount = inventory.ReferenceMetadataCount;
        using var mutated = inventory.WithSourceContent(BoundedProofPath,
            source.Replace("=> 1", "=> 2", StringComparison.Ordinal));
        _ = CurrentClassification(mutated, BoundedProofFqn);

        Assert.True(metadataCount > 0, "the snapshot must own in-memory compilation metadata");
        Assert.Equal(metadataCount, mutated.ReferenceMetadataCount);
        Assert.Equal(2, inventory.ReferenceLeaseCount);
        Assert.True(mutated.CacheEntryCount > 0, "the mutation snapshot must record its own bounded cache entries");

        mutated.Dispose();
        Assert.Equal(1, inventory.ReferenceLeaseCount);
        Assert.Equal(0, mutated.CacheEntryCount);
        Assert.False(inventory.ReferencesDisposed);

        inventory.Dispose();
        Assert.Equal(0, inventory.CacheEntryCount);
        Assert.True(inventory.ReferencesDisposed);
    }
}
