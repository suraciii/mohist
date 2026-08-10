namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationProvenance
{
    public int SchemaVersion { get; set; }
    public string? Pr { get; set; }
    public string? Parent { get; set; }
    public string? Commit { get; set; }
    public int RawRenameCount { get; set; }
    public int SpecRenameCount { get; set; }
    public int SupportRenameCount { get; set; }
    public int DeleteCount { get; set; }
    public int FocusedCaseCount { get; set; }
    public int MovedCaseCount { get; set; }
    public int AlreadyOwnedCaseCount { get; set; }
    public string? SourceCaseManifestDigest { get; set; }
    public string? ValidationHead { get; set; }
    public string? ValidationTree { get; set; }
    public int ValidationSourceFileCount { get; set; }
    public string? ValidationSourceTreeDigest { get; set; }
    public string? ValidationBaselineDigest { get; set; }

    internal static SpecUnitMigrationProvenance Read()
        => SpecUnitMigrationLedger.ReadResource<SpecUnitMigrationProvenance>("SpecUnitMigrationProvenance.json", "Embedded PR provenance is empty.");

    internal IReadOnlyList<SpecUnitMigrationProvenanceChange> Changes
        => SpecUnitMigrationGitProof.Read().Changes.Select(change => new SpecUnitMigrationProvenanceChange(
            change.RawStatus, change.Operation, change.SourcePath, change.SourceFqn, change.TargetPath, change.TargetFqn,
            change.SourceContentDigest)).ToArray();

    internal string ComputeSourceCaseManifestDigest()
        => SpecUnitMigrationInventory.Digest(Changes.Select(change => string.Join("|", change.RawStatus ?? "<missing>",
            change.Operation, change.SourcePath ?? "<missing>", change.SourceFqn ?? "<missing>", change.TargetPath ?? "<missing>",
            change.TargetFqn ?? "<missing>", change.SourceContentDigest)));

    internal static string? NormalizeTestPath(string? path)
        => path?.StartsWith("packages/server/tests/", StringComparison.Ordinal) == true
            ? path["packages/server/tests/".Length..] : path;

}

internal sealed record SpecUnitMigrationProvenanceChange(
    string? RawStatus,
    string Operation,
    string? SourcePath,
    string? SourceFqn,
    string? TargetPath,
    string? TargetFqn,
    string SourceContentDigest);
