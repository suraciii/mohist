using Xunit;

namespace Mohist.Server.ArchTests;

public sealed partial class SpecUnitMigrationLedgerRules
{
    private static ArchitectureRules.EmbeddedSource Source(string path, string content)
        => new(path, content, System.Text.Encoding.UTF8.GetByteCount(content));

    private static SpecUnitMigrationInventory CreateBoundedLiveInventory(string source)
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName("SpecUnitMigrationBoundedProof"),
            System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("SpecUnitMigrationBoundedProof");
        var type = module.DefineType(BoundedProofFqn,
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed);
        var method = type.DefineMethod("CompiledCase",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            typeof(void), Type.EmptyTypes);
        var factConstructor = typeof(FactAttribute).GetConstructor([typeof(string), typeof(int)])
            ?? throw new InvalidOperationException("xUnit FactAttribute source constructor is unavailable");
        method.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
            factConstructor, [BoundedProofPath, 1]));
        method.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        _ = type.CreateType();

        var discovery = SpecUnitMigrationCompiledDiscovery.FromAssemblies([BoundedProofFqn], assembly);
        return SpecUnitMigrationInventory.Create([Source(BoundedProofPath, source)], discovery);
    }

    private static string BoundedSource(string body) => $$"""
        namespace Mohist.Server.SpecTests.Specs.Negative;
        public sealed class BoundedLedgerSpecs { {{body}} }
        """;

    private static SpecUnitMigrationLedgerRow BoundCurrentRow(SpecUnitMigrationCandidate candidate)
    {
        static SpecUnitMigrationEndpoint Endpoint(SpecUnitMigrationCandidate value)
            => new() { Path = value.Path, Fqn = value.Fqn };

        return new SpecUnitMigrationLedgerRow
        {
            Id = "bounded-live-proof",
            Kind = "current",
            Source = "bounded in-memory compiled and source proof",
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
                Classification = "source-light",
                Symbols = candidate.Closure.ToList(),
                Digest = candidate.ClosureIdentityDigest,
                Evidence = Evidence(candidate),
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

    private static SpecUnitMigrationCandidate CurrentClassification(SpecUnitMigrationInventory inventory, string fqn)
    {
        Assert.True(inventory.TryGetCurrentSpecClassification(fqn, out var classification),
            $"current Spec classification is missing: {fqn}");
        return classification;
    }

    private static SpecUnitMigrationInventory CreateProductionInventory()
    {
        var ledger = SpecUnitMigrationLedger.Read(LedgerResourceName);
        var specAssembly = typeof(Mohist.Server.SpecTests.Specs.SystemSpecs.WindowsServiceLifecycleSpecs).Assembly;
        var unitAssembly = typeof(Mohist.Server.UnitTests.SystemSpecs.WindowsInstallArgumentTests).Assembly;
        var scopes = (ledger.Rows ?? []).Where(row => row.Current?.Fqn is not null)
            .GroupBy(row => row.Current!.Fqn!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)group.SelectMany(row => row.Closure?.Symbols ?? [])
                    .Append(group.Key).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var sources = ArchitectureRulesSupport.EmbeddedSources("TestSources/");
        var inventory = SpecUnitMigrationInventory.CreateScoped(sources, scopes);
        var requestedFqns = inventory.CurrentSpecFqns.Concat((ledger.Rows ?? []).SelectMany(row => new[]
            {
                row.Current?.Fqn,
                row.Target?.Fqn,
                row.Executable?.Fqn,
            }).OfType<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        var discovery = SpecUnitMigrationCompiledDiscovery.FromAssemblies(requestedFqns, specAssembly, unitAssembly);
        var productionInventory = inventory.WithCompiledDiscovery(discovery);
        productionInventory.PrimeProductionProofs();
        return productionInventory;
    }

    private static string Evidence(SpecUnitMigrationCandidate candidate)
        => $"source-path={candidate.Path} source-fqn={candidate.Fqn} case-digest={candidate.ExecutableCaseIdentityDigest} source-content-digest={candidate.SourceContentDigest} executable-closure-digest={candidate.ClosureIdentityDigest} edges-digest={candidate.EdgesDigest}";
}
