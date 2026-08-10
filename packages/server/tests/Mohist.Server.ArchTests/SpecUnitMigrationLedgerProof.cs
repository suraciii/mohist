namespace Mohist.Server.ArchTests;

internal static class SpecUnitMigrationLedgerProof
{
    internal static void ValidatePr388(SpecUnitMigrationProvenance provenance, SpecUnitMigrationInventory inventory,
        IReadOnlyList<SpecUnitMigrationProvenanceChange> changes, ICollection<string> violations)
    {
        var proof = SpecUnitMigrationGitProof.Read();
        foreach (var violation in proof.Validate()) violations.Add($"GIT_PROOF {violation}");
        if (provenance.SourceCaseManifestDigest != SpecUnitMigrationLedgerValidator.ExpectedPr388SourceCaseManifestDigest
            || provenance.ComputeSourceCaseManifestDigest() != SpecUnitMigrationLedgerValidator.ExpectedPr388SourceCaseManifestDigest)
            violations.Add("embedded PR #388 source-content manifest digest is not bound to the independent Git proof");
        if (changes.Count != 42 || changes.Count(change => change.Operation == "rename-spec") != 39
            || changes.Count(change => change.Operation == "rename-support") != 1 || changes.Count(change => change.Operation == "delete-spec") != 2)
            violations.Add("independent PR #388 Git proof must contain 40 renames, 1 support rename, and 2 deletes");
        if (provenance.RawRenameCount != 40 || provenance.SpecRenameCount != 39 || provenance.SupportRenameCount != 1 || provenance.DeleteCount != 2)
            violations.Add("PR #388 provenance aggregate operation counts are not independently proven");

        var focused = changes.Where(change => change.Operation != "rename-support").ToArray();
        var compiled = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var change in focused)
        {
            if (change.TargetFqn is null || change.TargetPath is null
                || !inventory.TryGetExecutable(change.TargetFqn, change.TargetPath, out var executable)) continue;
            compiled[change.TargetFqn] = executable.CaseIdentities;
        }

        var proofCases = proof.Cases;
        var compiledIdentities = compiled.Values.SelectMany(value => value).ToArray();
        var moved = proofCases.Count(testCase => testCase.Kind == "moved");
        var alreadyOwned = proofCases.Count(testCase => testCase.Kind == "already-owned");
        if (compiledIdentities.Length != 355 || proofCases.Count != compiledIdentities.Length || moved != 334 || alreadyOwned != 21)
            violations.Add($"compiled executable identity reconciliation must be 355/334/21; compiled={compiledIdentities.Length}, proof={proofCases.Count}, moved={moved}, already-owned={alreadyOwned}");
        if (compiledIdentities.Distinct(StringComparer.Ordinal).Count() != compiledIdentities.Length)
            violations.Add("compiled executable identities must be unique across focused target types");

        var proofByFqn = proofCases.GroupBy(testCase => testCase.Fqn, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(testCase => testCase.Identity).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (fqn, identities) in compiled)
        {
            if (!proofByFqn.TryGetValue(fqn, out var expected))
            {
                violations.Add($"compiled focused endpoint has no independent case identity proof: {fqn}");
                continue;
            }
            if (!identities.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                violations.Add($"compiled case identity proof differs for {fqn}");
        }
        if (proofCases.Any(testCase => !compiled.ContainsKey(testCase.Fqn)))
            violations.Add("independent case identity proof contains an endpoint outside compiled focused targets");
        if (provenance.FocusedCaseCount != compiledIdentities.Length || provenance.MovedCaseCount != moved
            || provenance.AlreadyOwnedCaseCount != alreadyOwned || moved + alreadyOwned != compiledIdentities.Length)
            violations.Add("PR #388 provenance case aggregate is not derived from compiled executable identities");
    }

    internal static void ValidateKindsAndStatuses(IEnumerable<SpecUnitMigrationLedgerRow> rows, ICollection<string> violations)
    {
        foreach (var row in rows)
        {
            if (row.Kind is not ("current" or "historical"))
                violations.Add($"{row.Id}: ledger kind is not allowlisted: {row.Kind}");

            var valid = row.Kind == "current"
                ? row.Status is "MOVE" or "REVIEW" or "KEEP" or "BLOCKED"
                : row.Status is "MOVE" or "DELETE";
            if (!valid) violations.Add($"{row.Id}: ledger status {row.Status} is not valid for kind {row.Kind}");
        }
    }
}
