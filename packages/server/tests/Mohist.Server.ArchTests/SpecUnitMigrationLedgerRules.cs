using Xunit;

namespace Mohist.Server.ArchTests;

public sealed class SpecUnitMigrationLedgerRules
{
    private const string LedgerResourceName = "SpecUnitMigrationLedger.json";
    private static readonly Lazy<SpecUnitMigrationInventory> ProductionSourceInventory = new(CreateProductionSourceInventory);
    private static readonly Lazy<IReadOnlyList<string>> ProductionPotentialCurrentSpecFqns = new(
        () => ProductionSourceInventory.Value.PotentialCurrentSpecFqns);
    private static readonly Lazy<SpecUnitMigrationInventory> ProductionInventory = new(CreateProductionInventory);

    internal static void WarmProductionInventory()
    {
        var inventory = ProductionInventory.Value;
        _ = SpecUnitMigrationLedgerValidator.Validate(SpecUnitMigrationLedger.Read(LedgerResourceName), inventory);
    }

    [Fact]
    public void SpecUnitMigrationLedger_CoversEveryCurrentStaticLightSpec()
    {
        var ledger = SpecUnitMigrationLedger.Read(LedgerResourceName);
        var inventory = ProductionInventory.Value;
        var violations = SpecUnitMigrationLedgerValidator.Validate(ledger, inventory);

        Assert.True(violations.Count == 0, "Spec/Unit migration ledger violations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsUnclassifiedCandidateAndParseDiagnostics()
    {
        const string fqn = "Mohist.Server.SpecTests.Specs.Negative.UnclassifiedSpecs";
        var inventory = SpecUnitMigrationInventory.Create(
        [
            Source("Mohist.Server.SpecTests/Specs/Negative/UnclassifiedSpecs.cs", """
                using Xunit;
                namespace Mohist.Server.SpecTests.Specs.Negative;
                public class UnclassifiedSpecs { [Fact] public void IsARealCandidate() { } }
                """),
            Source("Mohist.Server.SpecTests/Specs/Negative/ParseBrokenSpecs.cs", """
                using Xunit;
                namespace Mohist.Server.SpecTests.Specs.Negative;
                public class ParseBrokenSpecs { [Fact] public void MissingBody( { } }
                """),
        ], SpecUnitMigrationCompiledDiscovery.ForTests((fqn,
            new SpecUnitMigrationMtpFacts(1, 0, 0, 1, SpecUnitMigrationInventory.Digest(["synthetic-case"]), false))));

        var violations = SpecUnitMigrationLedgerValidator.Validate(new SpecUnitMigrationLedger { Rows = [] }, inventory);
        Assert.Contains(violations, violation => violation.Contains("UNCLASSIFIED", StringComparison.Ordinal)
            && violation.Contains("UnclassifiedSpecs", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("PARSE_DIAGNOSTIC", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsUnresolvedAndAmbiguousSymbolsGlobally()
    {
        var unresolved = SpecUnitMigrationInventory.Create(
        [Source("Mohist.Server.SpecTests/Specs/Negative/UnknownDependencySpecs.cs", """
            using Xunit;
            namespace Mohist.Server.SpecTests.Specs.Negative;
            public class UnknownDependencySpecs : UnknownExternalBase
            { [Fact] public void UsesUnknownHelper() => UnknownHelper.Create(); }
            """)],
            SpecUnitMigrationCompiledDiscovery.ForTests(("Mohist.Server.SpecTests.Specs.Negative.UnknownDependencySpecs",
                new SpecUnitMigrationMtpFacts(1, 0, 0, 1, "synthetic", false))));
        var unresolvedCandidate = unresolved.CurrentSpecClassifications.Single(candidate => candidate.Fqn.EndsWith("UnknownDependencySpecs", StringComparison.Ordinal));
        Assert.Contains(unresolvedCandidate.Blockers, blocker => blocker.Contains("unresolved symbol UnknownExternalBase", StringComparison.Ordinal));
        Assert.Contains(unresolvedCandidate.Blockers, blocker => blocker.Contains("unresolved symbol UnknownHelper", StringComparison.Ordinal));

        var ambiguous = SpecUnitMigrationInventory.Create(
        [
            Source("Mohist.Server.SpecTests/Specs/Negative/FirstDuplicateHelper.cs", """
                namespace Mohist.Server.SpecTests.Specs.Negative.First;
                public static class DuplicateHelper { public static void Create() { } }
                """),
            Source("Mohist.Server.SpecTests/Specs/Negative/SecondDuplicateHelper.cs", """
                namespace Mohist.Server.SpecTests.Specs.Negative.Second;
                public static class DuplicateHelper { public static void Create() { } }
                """),
            Source("Mohist.Server.SpecTests/Specs/Negative/AmbiguousSpecs.cs", """
                using Xunit;
                using Mohist.Server.SpecTests.Specs.Negative.First;
                using Mohist.Server.SpecTests.Specs.Negative.Second;
                namespace Mohist.Server.SpecTests.Specs.Negative;
                public class AmbiguousSpecs { [Fact] public void UsesAmbiguousHelper() => DuplicateHelper.Create(); }
                """),
        ], SpecUnitMigrationCompiledDiscovery.ForTests(("Mohist.Server.SpecTests.Specs.Negative.AmbiguousSpecs",
            new SpecUnitMigrationMtpFacts(1, 0, 0, 1, "synthetic", false))));
        var ambiguousCandidate = ambiguous.CurrentSpecClassifications.Single(candidate => candidate.Fqn.EndsWith("AmbiguousSpecs", StringComparison.Ordinal));
        Assert.Contains(ambiguousCandidate.Blockers, blocker => blocker.Contains("ambiguous symbol DuplicateHelper", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsGitHistoryAndValidationHeadMutations()
    {
        var provenance = SpecUnitMigrationProvenance.Read();
        var ledger = SpecUnitMigrationLedger.Read(LedgerResourceName);
        ledger.ValidationHead = "34f0f666d988a3a38ba218cad088f1062a55d5e";
        Assert.Contains(SpecUnitMigrationLedgerValidator.Validate(ledger, ProductionInventory.Value),
            violation => violation.Contains("ledger validationHead must be", StringComparison.Ordinal));

        var change = provenance.Changes.First(change => change.Operation == "rename-spec");
        var row = ledger.Rows!.Single(candidate => candidate.Id == "rename-agent-availability");
        row.History!.Commit = "602efa6abd6fca3efcd43b66b47ba10a80d9fabb";
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateHistoricalRowForTests(row, ProductionInventory.Value, provenance),
            violation => violation.Contains("Git commit mismatch", StringComparison.Ordinal));

        row.History.Commit = SpecUnitMigrationLedgerValidator.Pr388Commit;
        row.History.SourcePath = change.SourcePath + ".mutated";
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateHistoricalRowForTests(row, ProductionInventory.Value, provenance),
            violation => violation.Contains("absent from embedded PR #388 raw Git provenance", StringComparison.Ordinal));

        var mutatedHead = SpecUnitMigrationLedger.Read(LedgerResourceName);
        var current = mutatedHead.Rows!.Single(candidate => candidate.Id == "current-windows-service-lifecycle");
        current.ValidationHead = "34f0f666d988a3a38ba218cad088f1062a55d5e";
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateRequiredRowFieldsForTests(current), violation =>
            violation.Contains("validationHead must be", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsRetiringAnExistingHistoricalTarget()
    {
        var row = SpecUnitMigrationLedger.Read(LedgerResourceName).Rows!
            .Single(candidate => candidate.Id == "rename-runner-terminal-status");
        row.Retired = true;
        row.RetirementReason = "synthetic retirement";

        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateHistoricalRowForTests(row, ProductionInventory.Value),
            violation => violation.Contains("retired historical target is still a compiled discoverable type", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsPlaceholderAndTamperedSourceTreeMetadata()
    {
        var placeholder = SpecUnitMigrationLedger.Read(LedgerResourceName);
        placeholder.ValidationSourceFileCount = -1;
        placeholder.ValidationSourceTreeDigest = "__RECOMPUTE_VALIDATION_SOURCE_TREE_DIGEST__";
        Assert.Contains(SpecUnitMigrationLedgerValidator.Validate(placeholder, ProductionInventory.Value), violation =>
            violation.Contains("ledger validation source tree metadata", StringComparison.Ordinal));

        var tampered = SpecUnitMigrationLedger.Read(LedgerResourceName);
        tampered.ValidationSourceFileCount++;
        tampered.ValidationSourceTreeDigest = new string('0', SpecUnitMigrationLedgerValidator.ValidationSourceTreeDigest.Length);
        Assert.Contains(SpecUnitMigrationLedgerValidator.Validate(tampered, ProductionInventory.Value), violation =>
            violation.Contains("ledger validation source tree metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsClosureEvidenceDigestAndCompiledDiscoveryMutations()
    {
        const string fqn = "Mohist.Server.SpecTests.Specs.Negative.TamperedClosureSpecs";
        var inventory = SpecUnitMigrationInventory.Create(
            [Source("Mohist.Server.SpecTests/Specs/Negative/TamperedClosureSpecs.cs", """
                using Xunit;
                namespace Mohist.Server.SpecTests.Specs.Negative;
                public class TamperedClosureSpecs { [Fact] public void IsARealCandidate() { } }
                """)],
            SpecUnitMigrationCompiledDiscovery.ForTests((fqn,
                new SpecUnitMigrationMtpFacts(1, 0, 0, 1, SpecUnitMigrationInventory.Digest(["synthetic-case"]), false))));
        var classification = CurrentClassification(inventory, fqn);
        var row = new SpecUnitMigrationLedgerRow
        {
            Id = "synthetic-current-row",
            Kind = "current",
            Source = "synthetic",
            Legacy = new SpecUnitMigrationEndpoint { Path = classification.Path, Fqn = fqn },
            Current = new SpecUnitMigrationEndpoint { Path = classification.Path, Fqn = fqn },
            Target = new SpecUnitMigrationEndpoint { Path = "Mohist.Server.UnitTests/Negative/TamperedClosureTests.cs", Fqn = "Mohist.Server.UnitTests.Negative.TamperedClosureTests" },
            Discovered = new SpecUnitMigrationCounts { FactMethods = classification.FactMethods, TheoryMethods = classification.TheoryMethods, InlineDataRows = classification.InlineDataRows, MtpCases = classification.ExecutableCaseCount },
            Executable = new SpecUnitMigrationExecutable { Path = classification.Path, Fqn = fqn, CaseCount = classification.ExecutableCaseCount, CaseIdentityDigest = classification.ExecutableCaseIdentityDigest, ClosureIdentityDigest = classification.ClosureIdentityDigest, SourceContentDigest = classification.SourceContentDigest },
            Status = "MOVE",
            Closure = new SpecUnitMigrationClosure { Symbols = [], Digest = classification.ClosureIdentityDigest, Evidence = Evidence(classification) },
            Owner = "Mohist.Server.UnitTests.Negative.TamperedClosureTests",
            MoveContract = new SpecUnitMigrationMoveContract
            {
                Owner = "Mohist.Server.UnitTests.Negative.TamperedClosureTests",
                Helpers = [new SpecUnitMigrationMoveHelper { Role = "time" }],
                Target = new SpecUnitMigrationEndpoint { Path = "Mohist.Server.UnitTests/Negative/TamperedClosureTests.cs", Fqn = "Mohist.Server.UnitTests.Negative.TamperedClosureTests" },
                Split = new SpecUnitMigrationSplitBudget { MaxTargetLines = 299, MaxTargetFiles = 1, MaxHelperFiles = 1 },
            },
            History = new SpecUnitMigrationRowHistory { Pr = "#423", Operation = "residual-review", Commit = SpecUnitMigrationLedgerValidator.ValidationHead, SourcePath = classification.Path, SourceFqn = fqn, SourceContentDigest = classification.SourceContentDigest },
            ValidationHead = SpecUnitMigrationLedgerValidator.ValidationHead,
        };

        row.Closure!.Evidence = row.Closure.Evidence!.Replace("edges-digest=", "edges-digest=mutated-", StringComparison.Ordinal);
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateCurrentRowForTests(row, classification, inventory),
            violation => violation.Contains("closure evidence is not bound", StringComparison.Ordinal));

        row.Closure.Evidence = Evidence(classification);
        row.Closure.Digest = "mutated-closure-digest";
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateCurrentRowForTests(row, classification, inventory),
            violation => violation.Contains("closure digest mismatch", StringComparison.Ordinal));

        row.Closure.Digest = classification.ClosureIdentityDigest;
        row.Discovered!.MtpCases = row.Discovered.MtpCases + 1;
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateCurrentRowForTests(row, classification, inventory),
            violation => violation.Contains("compiled MTP discovery mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsFailIfKeepAndNonexistentCurrentTarget()
    {
        var inventory = ProductionInventory.Value;
        var ledger = SpecUnitMigrationLedger.Read(LedgerResourceName);
        var failIf = ledger.Rows!.Single(candidate => candidate.Id == "current-fail-if-marker-review");
        failIf.Kind = "current";
        Assert.Contains(SpecUnitMigrationLedgerValidator.Validate(ledger, inventory),
            violation => violation.Contains("STALE current row", StringComparison.Ordinal));

        var windows = ledger.Rows!.Single(candidate => candidate.Id == "current-windows-service-lifecycle");
        windows.Target!.Path = "Mohist.Server.UnitTests/SystemSpecs/NonexistentTests.cs";
        windows.Target.Fqn = "Mohist.Server.UnitTests.SystemSpecs.NonexistentTests";
        Assert.Contains(SpecUnitMigrationLedgerValidator.ValidateMovedRowForTests(windows, inventory),
            violation => violation.Contains("not a compiled discoverable type", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsFabricatedDeleteTargetAndBlobMutation()
    {
        var proofText = SpecUnitMigrationGitProof.ReadText();
        var deleteMap = proofText.Split('\n').Single(line => line.StartsWith("map|delete-spec|packages/server/tests/Mohist.Server.SpecTests/Specs/Issue/Domain/IssueManualDoneDomainSpecs.cs|", StringComparison.Ordinal));
        var fabricatedTarget = proofText.Replace(deleteMap,
            deleteMap.Replace("IssueManualCompletionTests.cs", "FabricatedCompletionTests.cs", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(SpecUnitMigrationGitProof.Parse(fabricatedTarget).Validate(), violation =>
            violation.Contains("mapped source/target object endpoint is missing", StringComparison.Ordinal));

        var objectLine = proofText.Split('\n').Single(line => line.StartsWith("object|parent|packages/server/tests/Mohist.Server.SpecTests/Specs/Issue/Domain/IssueManualDoneDomainSpecs.cs|", StringComparison.Ordinal));
        var fabricatedBlob = proofText.Replace(objectLine,
            objectLine[..^40] + new string('0', 40), StringComparison.Ordinal);
        Assert.Contains(SpecUnitMigrationGitProof.Parse(fabricatedBlob).Validate(), violation =>
            violation.Contains("parent blob object binding mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsPr388ModifiedOldBlobMutation()
    {
        var proofText = SpecUnitMigrationGitProof.ReadText();
        var modified = proofText.Split('\n').Single(line => line.StartsWith(
            "raw|M|packages/server/tests/Mohist.Server.UnitTests/Issue/Domain/IssueManualCompletionTests.cs|", StringComparison.Ordinal));
        var fields = modified.Split('|');
        fields[4] = new string('0', 40);
        var mutated = proofText.Replace(modified, string.Join('|', fields), StringComparison.Ordinal);

        Assert.Contains(SpecUnitMigrationGitProof.Parse(mutated).Validate(), violation =>
            violation.Contains("parent blob object binding mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsUnknownKindAndStatus()
    {
        var rows = SpecUnitMigrationLedger.Read(LedgerResourceName).Rows!;
        var mutated = rows.Single(row => row.Id == "current-windows-service-lifecycle");
        mutated.Kind = "future";
        mutated.Status = "FUTURE";
        var violations = new List<string>();

        SpecUnitMigrationLedgerProof.ValidateKindsAndStatuses(rows, violations);

        Assert.Contains(violations, violation => violation.Contains("ledger kind is not allowlisted", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("status FUTURE is not valid", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsPlannedUnitContractMutations()
    {
        var rows = SpecUnitMigrationLedger.Read(LedgerResourceName).Rows!;
        var hub = rows.Single(row => row.Id == "current-mohist-hub");
        hub.Kind = "current";
        hub.Target = hub.Current;

        var violations = SpecUnitMigrationLedgerValidator.ValidateNamedRowsForTests(rows);

        Assert.Contains(violations, violation => violation.Contains("planned Unit migration must be recorded as a moved MOVE row", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("moved Unit target mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsMoveBudgetAtOrAboveThreeHundredLines()
    {
        var rows = SpecUnitMigrationLedger.Read(LedgerResourceName).Rows!;
        var windows = rows.Single(row => row.Id == "current-windows-service-lifecycle");
        windows.MoveContract!.Split!.MaxTargetLines = 300;

        var violations = SpecUnitMigrationLedgerValidator.ValidatePlannedUnitTargetForTests(windows);

        Assert.Contains(violations, violation => violation.Contains("less than 300", StringComparison.Ordinal));
    }

    [Fact]
    public void SpecUnitMigrationLedger_NegativeProof_RejectsMasterBaselineProofMutation()
    {
        var proofText = SpecUnitMigrationGitProof.ReadText();
        var mutated = proofText.Replace("validation-head|2c96e43e2bc89fcfbd4e051576faec8f2861a8a8",
            "validation-head|34f0f666d988a3a38ba218cad088f1062a55dd5d", StringComparison.Ordinal);

        Assert.Contains(SpecUnitMigrationGitProof.Parse(mutated).Validate(), violation =>
            violation.Contains("validation-head", StringComparison.Ordinal));
    }

    private static SpecUnitMigrationInventory CreateProductionInventory()
    {
        var specAssembly = typeof(Mohist.Server.SpecTests.Specs.Agent.Api.AgentSessionLaunchValidationRoutesSpecs).Assembly;
        var unitAssembly = typeof(Mohist.Server.UnitTests.SystemSpecs.WindowsInstallArgumentTests).Assembly;
        var ledger = SpecUnitMigrationLedger.Read(LedgerResourceName);
        var requestedFqns = (ledger.Rows ?? []).SelectMany(row => new[]
            {
                row.Legacy?.Fqn,
                row.Current?.Fqn,
                row.Target?.Fqn,
                row.Executable?.Fqn,
            })
            .Where(fqn => !string.IsNullOrWhiteSpace(fqn)).Cast<string>()
            .Concat(ProductionPotentialCurrentSpecFqns.Value)
            .ToHashSet(StringComparer.Ordinal);
        var discovery = SpecUnitMigrationCompiledDiscovery.FromAssemblies(requestedFqns, specAssembly, unitAssembly);
        return ProductionSourceInventory.Value.BindCompiledDiscovery(discovery);
    }

    private static SpecUnitMigrationInventory CreateProductionSourceInventory()
        => SpecUnitMigrationInventory.Create(ArchitectureRulesSupport.EmbeddedSources("TestSources/"));

    private static ArchitectureRules.EmbeddedSource Source(string path, string content)
        => new(path, content, System.Text.Encoding.UTF8.GetByteCount(content));

    private static SpecUnitMigrationCandidate CurrentClassification(SpecUnitMigrationInventory inventory, string fqn)
    {
        Assert.True(inventory.TryGetCurrentSpecClassification(fqn, out var candidate), $"Current Spec classification not found: {fqn}");
        return candidate;
    }

    private static string Evidence(SpecUnitMigrationCandidate candidate)
        => $"source-path={candidate.Path} source-fqn={candidate.Fqn} case-digest={candidate.ExecutableCaseIdentityDigest} source-content-digest={candidate.SourceContentDigest} executable-closure-digest={candidate.ClosureIdentityDigest} edges-digest={candidate.EdgesDigest}";
}
