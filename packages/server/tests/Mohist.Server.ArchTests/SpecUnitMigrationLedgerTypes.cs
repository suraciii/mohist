using System.Text.Json;

namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationLedger
{
    public int SchemaVersion { get; set; }
    public string? ValidationHead { get; set; }
    public int ValidationSourceFileCount { get; set; }
    public string? ValidationSourceTreeDigest { get; set; }
    public string? ValidationBaselineDigest { get; set; }
    public string? ValidationHeadMeaning { get; set; }
    public SpecUnitMigrationHistory? History { get; set; }
    public List<SpecUnitMigrationLedgerRow>? Rows { get; set; }

    internal static SpecUnitMigrationLedger Read(string resourceName)
        => ReadResource<SpecUnitMigrationLedger>(resourceName, "Embedded migration ledger is empty.");

    internal static T ReadResource<T>(string resourceName, string emptyMessage)
    {
        using var stream = typeof(SpecUnitMigrationLedgerRules).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException(emptyMessage);
    }
}

internal sealed class SpecUnitMigrationLedgerRow
{
    public string? Id { get; set; }
    public string? Kind { get; set; }
    public string? Source { get; set; }
    public SpecUnitMigrationEndpoint? Legacy { get; set; }
    public SpecUnitMigrationEndpoint? Current { get; set; }
    public SpecUnitMigrationEndpoint? Target { get; set; }
    public SpecUnitMigrationCounts? Discovered { get; set; }
    public SpecUnitMigrationExecutable? Executable { get; set; }
    public string? Status { get; set; }
    public SpecUnitMigrationClosure? Closure { get; set; }
    public string? Owner { get; set; }
    public SpecUnitMigrationMoveContract? MoveContract { get; set; }
    public SpecUnitMigrationRowHistory? History { get; set; }
    public string? ValidationHead { get; set; }
}

internal sealed class SpecUnitMigrationEndpoint
{
    public string? Path { get; set; }
    public string? Fqn { get; set; }
}

internal sealed class SpecUnitMigrationCounts
{
    public int? FactMethods { get; set; }
    public int? TheoryMethods { get; set; }
    public int? InlineDataRows { get; set; }
    public int? MtpCases { get; set; }
}

internal sealed class SpecUnitMigrationClosure
{
    public string? Classification { get; set; }
    public List<string>? Symbols { get; set; }
    public string? Digest { get; set; }
    public string? Evidence { get; set; }
}

internal sealed class SpecUnitMigrationRowHistory
{
    public string? Operation { get; set; }
    public string? Pr { get; set; }
    public string? Commit { get; set; }
    public string? SourcePath { get; set; }
    public string? SourceFqn { get; set; }
    public string? SourceContentDigest { get; set; }
    public string? Note { get; set; }
}

internal sealed class SpecUnitMigrationHistory
{
    public int RawRenameCount { get; set; }
    public int SpecRenameCount { get; set; }
    public int SupportRenameCount { get; set; }
    public int DeleteCount { get; set; }
    public int ClaimedClassCount { get; set; }
    public int ClaimedFocusedCaseCount { get; set; }
    public int ClaimedMovedCaseCount { get; set; }
    public int ClaimedAlreadyOwnedCaseCount { get; set; }
    public int ClaimedUnitBefore { get; set; }
    public int ClaimedUnitAfter { get; set; }
    public int ClaimedSpecBefore { get; set; }
    public int ClaimedSpecAfter { get; set; }
    public string? SourceCaseManifestDigest { get; set; }
    public string? Reconciliation { get; set; }
}

internal sealed class SpecUnitMigrationMoveContract
{
    public string? Owner { get; set; }
    public List<SpecUnitMigrationMoveHelper>? Helpers { get; set; }
    public SpecUnitMigrationEndpoint? Target { get; set; }
    public SpecUnitMigrationSplitBudget? Split { get; set; }
}

internal sealed class SpecUnitMigrationMoveHelper
{
    public string? Role { get; set; }
}

internal sealed class SpecUnitMigrationSplitBudget
{
    public int? MaxTargetLines { get; set; }
    public int? MaxTargetFiles { get; set; }
    public int? MaxHelperFiles { get; set; }
}

internal sealed class SpecUnitMigrationExecutable
{
    public string? Path { get; set; }
    public string? Fqn { get; set; }
    public int? CaseCount { get; set; }
    public string? CaseIdentityDigest { get; set; }
    public string? ClosureIdentityDigest { get; set; }
    public string? SourceContentDigest { get; set; }
}
