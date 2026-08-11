using Xunit;

namespace Mohist.Server.ArchTests;

public sealed class SpecUnitMigrationSyntheticProofRules
{
    private const string RootPath = "Synthetic/LedgerSpecs.cs";
    private const string RootFqn = "Synthetic.LedgerSpecs";
    private const string HelperPath = "Synthetic/Helper.cs";
    private const string HelperFqn = "Synthetic.Helper";

    [Fact]
    public void ValidProofAndSemanticErrorAreFullyCompiledInTheTestBody()
    {
        var valid = Compile("public int Case() => Helper.Value();", "public static int Value() => 1;");
        var validProof = valid.Bind(valid.CompleteDiscovery("synthetic-case"));
        Assert.Empty(valid.Diagnostics);
        Assert.Empty(Validate(Row(validProof), validProof));

        var invalid = Compile("public int Case() => Helper.Value();", "public static int Value() => \"wrong\";");
        var invalidProof = invalid.Bind(invalid.CompleteDiscovery("synthetic-case"));

        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Contains("CS0029", StringComparison.Ordinal)
            && diagnostic.Contains(HelperPath, StringComparison.Ordinal));
        Assert.Contains(Validate(Row(invalidProof), invalidProof), violation =>
            violation.Contains("blocked current row cannot escape", StringComparison.Ordinal));
    }

    [Fact]
    public void StaleDiscoveryCannotBindToMutatedSource()
    {
        var original = Compile("public int Case() => Helper.Value();", "public static int Value() => 1;");
        var originalDiscovery = original.CompleteDiscovery("synthetic-case");
        var originalProof = original.Bind(originalDiscovery);
        var mutated = Compile("public int Case() => Helper.Value() + 1;", "public static int Value() => 1;");
        var staleProof = mutated.Bind(originalDiscovery);

        Assert.Contains(staleProof.Candidate.Blockers, blocker =>
            blocker.Contains("compiled discovery/source snapshot mismatch", StringComparison.Ordinal));
        var violations = Validate(Row(originalProof), staleProof);
        Assert.Contains(violations, violation => violation.Contains("blocked current row cannot escape", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("source-content digest mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void MethodAndHelperMutationsRebindCompleteClosureFacts()
    {
        var original = Compile("public int Case() => Helper.Value();", "public static int Value() => 1;");
        var originalProof = original.Bind(original.CompleteDiscovery("synthetic-case"));
        var methodMutation = Compile("public int Case() => Helper.Value() + 1;", "public static int Value() => 1;");
        var methodProof = methodMutation.Bind(methodMutation.CompleteDiscovery("synthetic-case"));
        var helperMutation = Compile("public int Case() => Helper.Value();", "public static int Value() => 2;");
        var helperProof = helperMutation.Bind(helperMutation.CompleteDiscovery("synthetic-case"));

        Assert.Empty(methodMutation.Diagnostics);
        Assert.Empty(helperMutation.Diagnostics);
        AssertMutationRejected(originalProof, methodProof);
        AssertMutationRejected(originalProof, helperProof);
    }

    [Fact]
    public void BaseExternalAndIncompleteFactsRemainBlocked()
    {
        var baseSnapshot = SpecUnitMigrationSyntheticProof.Compile(
            Source(RootPath, RootFqn, "namespace Synthetic; public class FixtureBase { } public sealed class LedgerSpecs : FixtureBase { public int Case() => 1; }"));
        var baseProof = baseSnapshot.Bind(baseSnapshot.CompleteDiscovery("synthetic-case"), "fixture/spec base FixtureBase");
        var externalSnapshot = SpecUnitMigrationSyntheticProof.Compile(
            Source(RootPath, RootFqn, "namespace Synthetic; public sealed class ExternalPort { } public sealed class LedgerSpecs { public object Case() => new ExternalPort(); }"));
        var externalProof = externalSnapshot.Bind(externalSnapshot.CompleteDiscovery("synthetic-case"), "external boundary ExternalPort");
        var completeSnapshot = Compile("public int Case() => Helper.Value();", "public static int Value() => 1;");
        var incompleteProof = completeSnapshot.Bind(new SpecUnitMigrationSyntheticProof.Discovery(
            completeSnapshot.SourceDigest, 1, 0, 0, false, ["synthetic-case"]));

        Assert.Empty(baseSnapshot.Diagnostics);
        Assert.Empty(externalSnapshot.Diagnostics);
        AssertBlocked(baseProof, "fixture/spec base FixtureBase");
        AssertBlocked(externalProof, "external boundary ExternalPort");
        AssertBlocked(incompleteProof, "compiled discovery incomplete");
    }

    [Fact]
    public void IndependentSnapshotsDoNotShareMutationStateOrResources()
    {
        var first = Compile("public int Case() => Helper.Value();", "public static int Value() => 1;");
        var firstProof = first.Bind(first.CompleteDiscovery("first-case"));
        var firstDigest = first.SourceDigest;
        var second = Compile("public int Case() => Helper.Value();", "public static int Value() => 2;");
        var secondProof = second.Bind(second.CompleteDiscovery("second-case"));

        Assert.Equal(firstDigest, first.SourceDigest);
        Assert.NotEqual(first.SourceDigest, second.SourceDigest);
        Assert.NotEqual(firstProof.Candidate.ExecutableCaseIdentityDigest, secondProof.Candidate.ExecutableCaseIdentityDigest);
        Assert.Empty(Validate(Row(firstProof), firstProof));
        Assert.Empty(Validate(Row(secondProof), secondProof));
    }

    private static SpecUnitMigrationSyntheticProof.Snapshot Compile(string rootMember, string helperMember)
        => SpecUnitMigrationSyntheticProof.Compile(
            Source(RootPath, RootFqn, $"namespace Synthetic; public sealed class LedgerSpecs {{ {rootMember} }}"),
            [Source(HelperPath, HelperFqn, $"namespace Synthetic; public static class Helper {{ {helperMember} }}")],
            [new SpecUnitMigrationSyntheticProof.Edge(RootFqn, HelperFqn)]);

    private static SpecUnitMigrationSyntheticProof.Source Source(string path, string fqn, string content)
        => new(path, fqn, content);

    private static IReadOnlyList<string> Validate(
        SpecUnitMigrationLedgerRow row, SpecUnitMigrationSyntheticProof.Bound proof)
        => SpecUnitMigrationLedgerValidator.ValidateCurrentRowForTests(row, proof.Candidate, proof.Executable);

    private static void AssertMutationRejected(
        SpecUnitMigrationSyntheticProof.Bound original, SpecUnitMigrationSyntheticProof.Bound mutated)
    {
        var violations = Validate(Row(original), mutated);
        Assert.Contains(violations, violation => violation.Contains("source-content digest mismatch", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("closure digest mismatch", StringComparison.Ordinal));
    }

    private static void AssertBlocked(SpecUnitMigrationSyntheticProof.Bound proof, string blocker)
    {
        Assert.Contains(proof.Candidate.Blockers, value => value.Contains(blocker, StringComparison.Ordinal));
        Assert.Contains(Validate(Row(proof), proof), violation =>
            violation.Contains("blocked current row cannot escape", StringComparison.Ordinal));
    }

    private static SpecUnitMigrationLedgerRow Row(SpecUnitMigrationSyntheticProof.Bound proof)
    {
        var candidate = proof.Candidate;
        static SpecUnitMigrationEndpoint Endpoint(SpecUnitMigrationCandidate value)
            => new() { Path = value.Path, Fqn = value.Fqn };

        return new SpecUnitMigrationLedgerRow
        {
            Id = "synthetic-current",
            Kind = "current",
            Source = "in-memory synthetic proof",
            Legacy = Endpoint(candidate),
            Current = Endpoint(candidate),
            Target = Endpoint(candidate),
            Discovered = new SpecUnitMigrationCounts
            {
                FactMethods = candidate.FactMethods,
                TheoryMethods = candidate.TheoryMethods,
                InlineDataRows = candidate.InlineDataRows,
                MtpCases = candidate.ExecutableCaseCount,
            },
            Executable = new SpecUnitMigrationExecutable
            {
                Path = candidate.Path,
                Fqn = candidate.Fqn,
                CaseCount = candidate.ExecutableCaseCount,
                CaseIdentityDigest = candidate.ExecutableCaseIdentityDigest,
                ClosureIdentityDigest = candidate.ClosureIdentityDigest,
                SourceContentDigest = candidate.SourceContentDigest,
            },
            Status = "KEEP",
            Closure = new SpecUnitMigrationClosure
            {
                Classification = "synthetic",
                Symbols = candidate.Closure.ToList(),
                Digest = candidate.ClosureIdentityDigest,
                Evidence = $"source-path={candidate.Path} source-fqn={candidate.Fqn} case-digest={candidate.ExecutableCaseIdentityDigest} "
                    + $"source-content-digest={candidate.SourceContentDigest} executable-closure-digest={candidate.ClosureIdentityDigest} edges-digest={candidate.EdgesDigest}",
            },
            Owner = candidate.Fqn,
            History = new SpecUnitMigrationRowHistory
            {
                Operation = "current-baseline",
                Pr = "#423",
                Commit = SpecUnitMigrationLedgerValidator.ValidationHead,
                SourcePath = candidate.Path,
                SourceFqn = candidate.Fqn,
                SourceContentDigest = candidate.SourceContentDigest,
            },
            ValidationHead = SpecUnitMigrationLedgerValidator.ValidationHead,
        };
    }
}
