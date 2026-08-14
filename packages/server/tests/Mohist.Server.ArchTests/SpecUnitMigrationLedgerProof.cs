namespace Mohist.Server.ArchTests;

internal static class SpecUnitMigrationLedgerProof
{
    internal static void ValidatePr388(SpecUnitMigrationProvenance provenance,
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

        var proofCases = proof.Cases;
        var moved = proofCases.Count(testCase => testCase.Kind == "moved");
        var alreadyOwned = proofCases.Count(testCase => testCase.Kind == "already-owned");
        if (proofCases.Count != 355 || moved != 334 || alreadyOwned != 21)
            violations.Add($"PR #388 case proof must reconcile to 355/334/21; actual={proofCases.Count}/{moved}/{alreadyOwned}");
        if (provenance.FocusedCaseCount != proofCases.Count || provenance.MovedCaseCount != moved
            || provenance.AlreadyOwnedCaseCount != alreadyOwned || moved + alreadyOwned != proofCases.Count)
            violations.Add("PR #388 provenance case aggregate differs from the historical case proof");
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
