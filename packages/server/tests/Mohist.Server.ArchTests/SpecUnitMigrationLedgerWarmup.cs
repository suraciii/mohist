using System.Runtime.CompilerServices;

namespace Mohist.Server.ArchTests;

internal static class SpecUnitMigrationLedgerWarmup
{
    [ModuleInitializer]
    internal static void Initialize() => SpecUnitMigrationLedgerRules.WarmProductionInventory();
}
