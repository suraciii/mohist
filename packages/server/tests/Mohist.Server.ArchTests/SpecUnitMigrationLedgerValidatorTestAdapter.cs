namespace Mohist.Server.ArchTests;

internal static partial class SpecUnitMigrationLedgerValidator
{
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

    internal static IReadOnlyList<string> ValidateCurrentRowForTests(
        SpecUnitMigrationLedgerRow row,
        SpecUnitMigrationCandidate classification,
        SpecUnitMigrationExecutableFacts executable)
    {
        var violations = new List<string>();
        ValidateCurrentRowCore(row, classification, (endpoint, targetViolations) =>
            ValidateExecutableAndClosure(row, endpoint, executable,
                fqn => fqn == classification.Fqn ? classification : null, targetViolations, "current"), violations);
        return violations;
    }

    internal static IReadOnlyList<string> ValidateNamedRowsForTests(IEnumerable<SpecUnitMigrationLedgerRow> rows)
    {
        var violations = new List<string>();
        ValidateRequiredNamedRows(rows.ToArray(), violations);
        return violations;
    }

    internal static IReadOnlyList<string> ValidateMovedRowForTests(
        SpecUnitMigrationLedgerRow row, SpecUnitMigrationInventory inventory)
    {
        var violations = new List<string>();
        ValidateMovedRow(row, inventory, violations);
        return violations;
    }

    internal static IReadOnlyList<string> ValidatePlannedUnitTargetForTests(SpecUnitMigrationLedgerRow row)
    {
        var violations = new List<string>();
        ValidatePlannedUnitTarget(row, violations);
        return violations;
    }
}
