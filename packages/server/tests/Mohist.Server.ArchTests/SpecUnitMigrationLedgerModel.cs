using System.Text.Json;

namespace Mohist.Server.ArchTests;

internal static class SpecUnitMigrationLedgerValidator
{
    internal const string ValidationHead = "2c96e43e2bc89fcfbd4e051576faec8f2861a8a8";
    internal const string ValidationTree = "ff159609b97df5b1fac6d6404a6d1811f6bda99a";
    internal const int ValidationSourceTreeFileCount = 834;
    internal const string ValidationSourceTreeDigest = "2c4c5f9128c8c641c30e31ecb96e2f7aea371c09abd19c36a1d6198de4a13d69";
    internal const string ExpectedValidationBaselineDigest = "03cad8aca4e8ddf9688d7b3532d03cb1b93c039e8a9ec4e0e38ee73e5b403c62";
    internal const string Pr388Commit = "602efa6abd6fca3efcd43b66b47ba10a80d9faba";
    internal const string Pr388Parent = "0f723ddb87dfd0943b32e5b53b9af9ccbc89367d";
    internal const string ExpectedPr388SourceCaseManifestDigest = "08f8da2c077df6f786dfc2dc0a65086e6ac1b56e09cde694c440765d405319fe";

    internal static IReadOnlyList<string> Validate(SpecUnitMigrationLedger ledger, SpecUnitMigrationInventory inventory)
    {
        var violations = new List<string>();
        var provenance = SpecUnitMigrationProvenance.Read();
        var rows = ledger.Rows ?? [];
        ValidateLedgerShape(ledger, rows, provenance, inventory, violations);
        foreach (var diagnostic in inventory.Diagnostics)
            violations.Add($"{(diagnostic.StartsWith("SEMANTIC|", StringComparison.Ordinal) ? "SEMANTIC_DIAGNOSTIC" : "PARSE_DIAGNOSTIC")} {diagnostic}");

        var currentRows = rows.Where(row => row.Kind == "current").ToArray();
        var currentByFqn = currentRows.Where(row => row.Current?.Fqn is not null)
            .GroupBy(row => row.Current!.Fqn!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var candidate in inventory.CurrentStaticLightSpecs)
        {
            if (!currentByFqn.TryGetValue(candidate.Fqn, out var matches) || matches.Length != 1)
            {
                violations.Add($"UNCLASSIFIED {candidate.Fqn} at {candidate.Path}; blockers=[{string.Join(", ", candidate.Blockers)}]");
            }
        }

        foreach (var row in currentRows)
        {
            if (row.Current?.Fqn is null || !inventory.TryGetCurrentSpecClassification(row.Current.Fqn, out var classification))
            {
                violations.Add($"STALE current row {row.Id}: {row.Current?.Path}/{row.Current?.Fqn}");
                continue;
            }
            ValidateCurrentRow(row, classification, inventory, violations);
        }

        foreach (var row in rows.Where(row => row.Kind == "historical"))
            ValidateHistoricalRow(row, inventory, provenance, violations);
        ValidateRequiredNamedRows(currentRows, violations);
        return violations;
    }

    internal static IReadOnlyList<string> ValidateHistoricalRowForTests(
        SpecUnitMigrationLedgerRow row, SpecUnitMigrationInventory inventory, SpecUnitMigrationProvenance? provenance = null)
    {
        var violations = new List<string>();
        ValidateHistoricalRow(row, inventory, provenance ?? SpecUnitMigrationProvenance.Read(), violations);
        return violations;
    }

    internal static IReadOnlyList<string> ValidateCurrentRowForTests(
        SpecUnitMigrationLedgerRow row, SpecUnitMigrationCandidate classification, SpecUnitMigrationInventory inventory)
    {
        var violations = new List<string>();
        ValidateCurrentRow(row, classification, inventory, violations);
        return violations;
    }

    private static void ValidateCurrentRow(SpecUnitMigrationLedgerRow row, SpecUnitMigrationCandidate classification,
        SpecUnitMigrationInventory inventory, ICollection<string> violations)
    {
        if (row.Status is not ("MOVE" or "REVIEW" or "KEEP" or "BLOCKED"))
            violations.Add($"{row.Id}: invalid status {row.Status}");
        if (classification.Blockers.Count > 0 && row.Status is not "BLOCKED")
            violations.Add($"{row.Id}: blocked current row cannot escape as {row.Status}; blockers=[{string.Join("; ", classification.Blockers)}]");
        if (classification.Blockers.Count == 0 && row.Status == "BLOCKED")
            violations.Add($"{row.Id}: unblocked current row cannot be BLOCKED");
        if (row.Current?.Path != classification.Path)
            violations.Add($"{row.Id}: current source path mismatch; ledger={row.Current?.Path}, compiled={classification.Path}");
        if (row.Discovered is null || row.Discovered.FactMethods != classification.FactMethods || row.Discovered.TheoryMethods != classification.TheoryMethods
            || row.Discovered.InlineDataRows != classification.InlineDataRows || row.Discovered.MtpCases != classification.ExecutableCaseCount)
            violations.Add($"{row.Id}: compiled MTP discovery mismatch; ledger={Counts(row.Discovered)}, compiled={classification.FactMethods}/{classification.TheoryMethods}/{classification.InlineDataRows}/{classification.ExecutableCaseCount}");
        var plannedTarget = row.MoveContract is not null;
        if (plannedTarget) ValidatePlannedUnitTarget(row, violations);
        var expectedOwner = plannedTarget || row.Status == "MOVE" ? row.Target?.Fqn : row.Current?.Fqn;
        if (row.Owner != expectedOwner) violations.Add($"{row.Id}: owner binding mismatch; owner={row.Owner}, expected={expectedOwner}");
        var expectedEndpoint = plannedTarget || row.Status != "MOVE" ? row.Current : row.Target;
        ValidateExecutableAndClosure(row, expectedEndpoint, inventory, violations, "current");
        ValidateCurrentHistory(row, classification, violations);
        ValidateRequiredRowFields(row, violations);
    }

    private static void ValidateHistoricalRow(SpecUnitMigrationLedgerRow row, SpecUnitMigrationInventory inventory,
        SpecUnitMigrationProvenance provenance, ICollection<string> violations)
    {
        var history = row.History;
        if (history is null || row.Legacy is null || row.Target is null) return;
        if (history.Pr != "#388" || history.Commit != Pr388Commit)
            violations.Add($"{row.Id}: PR #388 Git commit mismatch; pr={history.Pr}, commit={history.Commit}");
        if (history.SourcePath != row.Legacy.Path || history.SourceFqn != row.Legacy.Fqn)
            violations.Add($"{row.Id}: history source differs from legacy endpoint");

        var change = provenance.Changes.SingleOrDefault(candidate => candidate.Operation == history.Operation
            && candidate.SourcePath == history.SourcePath && candidate.SourceFqn == history.SourceFqn);
        if (change is null)
        {
            violations.Add($"{row.Id}: source path/FQN/operation is absent from embedded PR #388 raw Git provenance");
            return;
        }
        if (row.Current?.Path != change.TargetPath || row.Current?.Fqn != change.TargetFqn
            || row.Target.Path != change.TargetPath || row.Target.Fqn != change.TargetFqn)
            violations.Add($"{row.Id}: current/target mapping differs from embedded PR #388 operation target");
        if (row.Owner != change.TargetFqn) violations.Add($"{row.Id}: owner binding mismatch; owner={row.Owner}, target={change.TargetFqn}");
        if (row.Status != (change.Operation == "delete-spec" ? "DELETE" : "MOVE"))
            violations.Add($"{row.Id}: history operation {change.Operation} has invalid status {row.Status}");
        if (history.SourceContentDigest != change.SourceContentDigest)
            violations.Add($"{row.Id}: history source-content digest differs from PR #388 source object");
        ValidateExecutableAndClosure(row, row.Target, inventory, violations, "target");
        ValidateRequiredRowFields(row, violations);
    }

    private static void ValidateExecutableAndClosure(SpecUnitMigrationLedgerRow row, SpecUnitMigrationEndpoint? expectedEndpoint,
        SpecUnitMigrationInventory inventory, ICollection<string> violations, string bindingKind)
    {
        if (row.Executable is null || expectedEndpoint is null) return;
        if (!inventory.TryGetExecutable(expectedEndpoint.Fqn ?? "", expectedEndpoint.Path ?? "", out var actualTarget))
        {
            violations.Add($"{row.Id}: executable {bindingKind} target endpoint is not a compiled discoverable type: {expectedEndpoint.Path}/{expectedEndpoint.Fqn}");
            return;
        }
        if (row.Executable.Path != expectedEndpoint.Path || row.Executable.Fqn != expectedEndpoint.Fqn)
        {
            violations.Add($"{row.Id}: executable {bindingKind} endpoint mismatch");
            return;
        }
        var actual = actualTarget;
        if (row.Executable.CaseCount != actual.CaseCount)
            violations.Add($"{row.Id}: executable case count mismatch; ledger={row.Executable.CaseCount}, actual={actual.CaseCount}");
        if (row.Executable.CaseIdentityDigest != actual.CaseIdentityDigest)
            violations.Add($"{row.Id}: executable case identity digest mismatch; ledger={row.Executable.CaseIdentityDigest}, actual={actual.CaseIdentityDigest}");
        if (row.Executable.SourceContentDigest != actual.SourceContentDigest)
            violations.Add($"{row.Id}: executable source-content digest mismatch; ledger={row.Executable.SourceContentDigest}, actual={actual.SourceContentDigest}");
        if (!inventory.TryGetCandidate(actual.Fqn, out var candidate))
        {
            violations.Add($"{row.Id}: executable closure cannot classify {actual.Fqn}");
            return;
        }
        var actualSymbols = row.Closure?.Symbols?.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? [];
        if (!candidate.Closure.SequenceEqual(actualSymbols, StringComparer.Ordinal))
            violations.Add($"{row.Id}: closure symbols mismatch");
        if (row.Executable.ClosureIdentityDigest != actual.ClosureIdentityDigest || row.Closure?.Digest != actual.ClosureIdentityDigest)
            violations.Add($"{row.Id}: closure digest mismatch; executable={row.Executable.ClosureIdentityDigest}, closure={row.Closure?.Digest}, actual={actual.ClosureIdentityDigest}");
        var evidence = row.Closure?.Evidence ?? "";
        var requiredEvidence = new[]
        {
            $"source-path={actual.Path}", $"source-fqn={actual.Fqn}", $"case-digest={actual.CaseIdentityDigest}", $"source-content-digest={actual.SourceContentDigest}",
            $"executable-closure-digest={actual.ClosureIdentityDigest}", $"edges-digest={actual.EdgesDigest}",
        };
        foreach (var token in requiredEvidence)
            if (!evidence.Contains(token, StringComparison.Ordinal)) violations.Add($"{row.Id}: closure evidence is not bound to {token}");
    }

    private static void ValidateCurrentHistory(SpecUnitMigrationLedgerRow row, SpecUnitMigrationCandidate classification, ICollection<string> violations)
    {
        if (row.History?.Pr != "#423" || row.History.Commit != ValidationHead)
            violations.Add($"{row.Id}: current validationHead history mismatch");
        if (row.History?.SourcePath != row.Current?.Path || row.History?.SourceFqn != row.Current?.Fqn)
            violations.Add($"{row.Id}: current history must match current source path/FQN");
        if (row.History?.SourceContentDigest != classification.SourceContentDigest)
            violations.Add($"{row.Id}: current history source-content digest mismatch");
    }

    private static void ValidateLedgerShape(SpecUnitMigrationLedger ledger, IReadOnlyList<SpecUnitMigrationLedgerRow> rows,
        SpecUnitMigrationProvenance provenance, SpecUnitMigrationInventory inventory, ICollection<string> violations)
    {
        if (ledger.SchemaVersion != 1) violations.Add($"ledger schemaVersion must be 1, got {ledger.SchemaVersion}");
        if (ledger.ValidationHead != ValidationHead) violations.Add($"ledger validationHead must be {ValidationHead}, got {ledger.ValidationHead}");
        if (inventory.SourceTree.ValidationHead != ValidationHead || inventory.SourceTree.ValidationTree != ValidationTree)
            violations.Add("current embedded source tree is not bound to the validation head/tree");
        if (inventory.SourceTree.FileCount != ledger.ValidationSourceFileCount
            || inventory.SourceTree.Digest != ledger.ValidationSourceTreeDigest
            || inventory.SourceTree.FileCount != ValidationSourceTreeFileCount
            || inventory.SourceTree.Digest != ValidationSourceTreeDigest)
            violations.Add($"current embedded source tree digest mismatch; files={inventory.SourceTree.FileCount}, digest={inventory.SourceTree.Digest}");
        if (ledger.ValidationBaselineDigest != ExpectedValidationBaselineDigest)
            violations.Add($"ledger validation baseline digest must be {ExpectedValidationBaselineDigest}, got {ledger.ValidationBaselineDigest}");
        if (string.IsNullOrWhiteSpace(ledger.ValidationHeadMeaning) || !ledger.ValidationHeadMeaning.Contains("not the final validation commit", StringComparison.OrdinalIgnoreCase))
            violations.Add("ledger validationHeadMeaning must distinguish the rebase baseline from final validation");
        if (provenance.SchemaVersion != 1 || provenance.Pr != "#388" || provenance.Parent != Pr388Parent || provenance.Commit != Pr388Commit
            || provenance.ValidationHead != ValidationHead || provenance.ValidationTree != ValidationTree
            || provenance.ValidationBaselineDigest != ExpectedValidationBaselineDigest
            || provenance.ValidationSourceFileCount != inventory.SourceTree.FileCount
            || provenance.ValidationSourceTreeDigest != inventory.SourceTree.Digest)
            violations.Add("embedded PR #388 provenance does not bind the exact parent and commit");
        var changes = provenance.Changes;
        SpecUnitMigrationLedgerProof.ValidatePr388(provenance, inventory, changes, violations);
        if (ledger.History is null) violations.Add("ledger history aggregate is required");
        else
        {
            var history = ledger.History;
            if (history.RawRenameCount != provenance.RawRenameCount || history.SpecRenameCount != provenance.SpecRenameCount
                || history.SupportRenameCount != provenance.SupportRenameCount || history.DeleteCount != provenance.DeleteCount
                || history.ClaimedClassCount != 41 || history.ClaimedFocusedCaseCount != provenance.FocusedCaseCount
                || history.ClaimedMovedCaseCount != provenance.MovedCaseCount || history.ClaimedAlreadyOwnedCaseCount != provenance.AlreadyOwnedCaseCount
                || history.SourceCaseManifestDigest != ExpectedPr388SourceCaseManifestDigest)
                violations.Add("ledger aggregate differs from independent embedded PR provenance");
        }
        SpecUnitMigrationLedgerProof.ValidateKindsAndStatuses(rows, violations);
        var historical = rows.Where(row => row.Kind == "historical").ToArray();
        var keys = historical.Select(row => $"{row.History?.Operation}|{row.History?.SourcePath}|{row.History?.SourceFqn}").ToArray();
        if (keys.Length != changes.Count || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length
            || changes.Any(change => !keys.Contains($"{change.Operation}|{change.SourcePath}|{change.SourceFqn}", StringComparer.Ordinal)))
            violations.Add("historical ledger rows are not a one-to-one set match with embedded raw Git provenance");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id ?? "")) violations.Add($"ledger row id is missing or duplicated: {row?.Id}");
            if (row is not null) ValidateRequiredRowFields(row, violations);
        }
    }

    private static void ValidateRequiredNamedRows(IReadOnlyList<SpecUnitMigrationLedgerRow> rows, ICollection<string> violations)
    {
        var windows = rows.SingleOrDefault(row => row.Id == "current-windows-service-lifecycle");
        if (windows is null || windows.Status != "MOVE") violations.Add("WindowsServiceLifecycleSpecs must be status MOVE");
        else ValidatePlannedNamedRow(windows, "MOVE", "Mohist.Server.UnitTests/SystemSpecs/WindowsServiceLifecycleTests.cs",
            "Mohist.Server.UnitTests.SystemSpecs.WindowsServiceLifecycleTests", violations);
        var failIf = rows.SingleOrDefault(row => row.Id == "current-fail-if-marker-review");
        if (failIf is null || failIf.Status != "BLOCKED") violations.Add("FailIfMarkerSpecs must be explicitly BLOCKED");
        else ValidatePlannedNamedRow(failIf, "BLOCKED", "Mohist.Server.UnitTests/Workflow/Grain/FailIfMarkerSemanticTests.cs",
            "Mohist.Server.UnitTests.Workflow.Grain.FailIfMarkerSemanticTests", violations);
        ValidatePlannedNamedRow(rows.SingleOrDefault(row => row.Id == "current-mohist-hub"), "MOVE",
            "Mohist.Server.UnitTests/Events/MohistHubTests.cs", "Mohist.Server.UnitTests.Events.MohistHubTests", violations);
        ValidatePlannedNamedRow(rows.SingleOrDefault(row => row.Id == "current-mohist-hub-project-affinity"), "MOVE",
            "Mohist.Server.UnitTests/Events/MohistHubProjectAffinityTests.cs", "Mohist.Server.UnitTests.Events.MohistHubProjectAffinityTests", violations);
    }

    internal static IReadOnlyList<string> ValidateNamedRowsForTests(IEnumerable<SpecUnitMigrationLedgerRow> rows)
    {
        var violations = new List<string>();
        ValidateRequiredNamedRows(rows.ToArray(), violations);
        return violations;
    }

    private static void ValidatePlannedNamedRow(SpecUnitMigrationLedgerRow? row, string status, string path, string fqn,
        ICollection<string> violations)
    {
        if (row is null)
        {
            violations.Add($"planned Unit row is missing: {fqn}");
            return;
        }
        if (row.Status != status)
            violations.Add($"{row.Id}: planned Unit status must be {status}");
        if (row.Target?.Path != path || row.Target.Fqn != fqn)
            violations.Add($"{row.Id}: planned Unit target mismatch");
        ValidatePlannedUnitTarget(row, violations);
    }

    private static void ValidatePlannedUnitTarget(SpecUnitMigrationLedgerRow row, ICollection<string> violations)
    {
        if (row.MoveContract is null)
        {
            violations.Add($"{row.Id}: structured MOVE contract is required");
            return;
        }
        if (row.MoveContract.Owner != row.Owner)
            violations.Add($"{row.Id}: structured MOVE owner must bind the row owner");
        if (row.Target is null || row.Target.Path?.StartsWith("Mohist.Server.UnitTests/", StringComparison.Ordinal) != true
            || row.Target.Fqn?.StartsWith("Mohist.Server.UnitTests.", StringComparison.Ordinal) != true)
            violations.Add($"{row.Id}: planned Unit target must be a Unit path/FQN");
        if (row.MoveContract.Target?.Path != row.Target?.Path || row.MoveContract.Target?.Fqn != row.Target?.Fqn)
            violations.Add($"{row.Id}: structured MOVE target must bind the row target");
        if (row.Current?.Path == row.Target?.Path || row.Current?.Fqn == row.Target?.Fqn)
            violations.Add($"{row.Id}: planned Unit target must not masquerade as the current Spec endpoint");
        var split = row.MoveContract.Split;
        if (split is null || split.MaxTargetLines is null || split.MaxTargetLines <= 0 || split.MaxTargetLines >= 300
            || split.MaxTargetFiles is not > 0 || split.MaxHelperFiles is not > 0)
            violations.Add($"{row.Id}: structured MOVE split budgets are required and target lines must be less than 300");
        var helpers = row.MoveContract.Helpers ?? [];
        if (helpers.Count == 0 || helpers.Any(helper => string.IsNullOrWhiteSpace(helper.Role))
            || helpers.Select(helper => helper.Role).Distinct(StringComparer.Ordinal).Count() != helpers.Count)
            violations.Add($"{row.Id}: structured MOVE helper roles must be unique and explicit");
        ValidateNamedMoveHelperRoles(row, helpers, violations);
    }

    private static void ValidateRequiredRowFields(SpecUnitMigrationLedgerRow row, ICollection<string> violations)
    {
        if (string.IsNullOrWhiteSpace(row.Kind)) violations.Add($"{row.Id}: kind is required");
        if (string.IsNullOrWhiteSpace(row.Source)) violations.Add($"{row.Id}: source is required");
        if (row.Legacy is null || string.IsNullOrWhiteSpace(row.Legacy.Path) || string.IsNullOrWhiteSpace(row.Legacy.Fqn)) violations.Add($"{row.Id}: legacy endpoint is required");
        if (row.Current is null || string.IsNullOrWhiteSpace(row.Current.Path) || string.IsNullOrWhiteSpace(row.Current.Fqn)) violations.Add($"{row.Id}: current endpoint is required");
        if (row.Target is null || string.IsNullOrWhiteSpace(row.Target.Path) || string.IsNullOrWhiteSpace(row.Target.Fqn)) violations.Add($"{row.Id}: target endpoint is required");
        if (row.Discovered is null || row.Discovered.MtpCases is null || row.Discovered.MtpCases < 0) violations.Add($"{row.Id}: compiled discovered counts are required");
        if (row.Executable is null || string.IsNullOrWhiteSpace(row.Executable.Path) || string.IsNullOrWhiteSpace(row.Executable.Fqn)
            || row.Executable.CaseCount is null || string.IsNullOrWhiteSpace(row.Executable.CaseIdentityDigest) || string.IsNullOrWhiteSpace(row.Executable.ClosureIdentityDigest)
            || string.IsNullOrWhiteSpace(row.Executable.SourceContentDigest))
            violations.Add($"{row.Id}: executable path/FQN/count/digests are required");
        if (string.IsNullOrWhiteSpace(row.Status) || row.Status == "UNCLASSIFIED") violations.Add($"{row.Id}: status must be explicit");
        if (row.Closure?.Symbols is null || string.IsNullOrWhiteSpace(row.Closure.Digest) || string.IsNullOrWhiteSpace(row.Closure.Evidence)) violations.Add($"{row.Id}: closure symbols/digest/evidence are required");
        if (string.IsNullOrWhiteSpace(row.Owner)) violations.Add($"{row.Id}: owner is required");
        if (row.Kind == "current" && row.Status is "MOVE" or "BLOCKED" && row.MoveContract is null)
            violations.Add($"{row.Id}: planned current structured MOVE contract is required");
        if (row.History is null || string.IsNullOrWhiteSpace(row.History.Operation) || string.IsNullOrWhiteSpace(row.History.Commit)
            || string.IsNullOrWhiteSpace(row.History.SourcePath) || string.IsNullOrWhiteSpace(row.History.SourceFqn)
            || string.IsNullOrWhiteSpace(row.History.SourceContentDigest)) violations.Add($"{row.Id}: history is required");
        if (row.ValidationHead != ValidationHead) violations.Add($"{row.Id}: validationHead must be {ValidationHead}");
    }

    private static string Counts(SpecUnitMigrationCounts? counts)
        => counts is null ? "<null>" : $"{counts.FactMethods}/{counts.TheoryMethods}/{counts.InlineDataRows}/{counts.MtpCases}";

    private static void ValidateNamedMoveHelperRoles(SpecUnitMigrationLedgerRow row,
        IReadOnlyList<SpecUnitMigrationMoveHelper> helpers, ICollection<string> violations)
    {
        var expected = row.Id switch
        {
            "current-windows-service-lifecycle" => new[] { "filesystem", "command", "process", "watcher", "time" },
            "current-mohist-hub" or "current-mohist-hub-project-affinity" => new[] { "events" },
            "current-fail-if-marker-review" => new[] { "serializer", "extension", "fixture" },
            _ => null,
        };
        if (expected is not null && !helpers.Select(helper => helper.Role).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            violations.Add($"{row.Id}: structured MOVE helper roles do not match the planned schema");
    }
}
