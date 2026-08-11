using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationCompiledDiscovery
{
    private readonly IReadOnlyDictionary<string, SpecUnitMigrationMtpFacts> _facts;

    private SpecUnitMigrationCompiledDiscovery(IReadOnlyDictionary<string, SpecUnitMigrationMtpFacts> facts) => _facts = facts;

    internal static SpecUnitMigrationCompiledDiscovery Empty { get; } = new(new Dictionary<string, SpecUnitMigrationMtpFacts>(StringComparer.Ordinal));

    internal static SpecUnitMigrationCompiledDiscovery ForTests(params (string Fqn, SpecUnitMigrationMtpFacts Facts)[] entries)
        => new(entries.ToDictionary(entry => entry.Fqn, entry => entry.Facts, StringComparer.Ordinal));

    internal static SpecUnitMigrationCompiledDiscovery FromAssemblies(
        IReadOnlySet<string> requestedFqns,
        params Assembly[] assemblies)
    {
        var cases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var requestedTypes = requestedFqns.Select(fqn => assembly.GetType(fqn, throwOnError: false))
                .Where(type => type is not null).Cast<Type>().ToArray();
            if (requestedTypes.Length == 0) continue;
            var testAssembly = new XunitTestAssembly(assembly);
            var discoverer = new ScopedXunitTestFrameworkDiscoverer(
                testAssembly, new CollectionPerClassTestCollectionFactory(testAssembly));
            var options = new InMemoryDiscoveryOptions();
            options.SetValue(TestOptionsNames.Discovery.PreEnumerateTheories, true);
            try
            {
                discoverer.FindTypes(testCase =>
                {
                    var fqn = testCase.TestClassName;
                    if (!string.IsNullOrWhiteSpace(fqn))
                    {
                        if (!cases.TryGetValue(fqn!, out var identities)) cases.Add(fqn!, identities = []);
                        identities.Add(string.Join("|", fqn, testCase.TestMethodName, testCase.TestCaseDisplayName));
                    }
                    return new ValueTask<bool>(true);
                }, options, requestedTypes).GetAwaiter().GetResult();
            }
            finally
            {
                discoverer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        var facts = new Dictionary<string, SpecUnitMigrationMtpFacts>(StringComparer.Ordinal);
        foreach (var group in cases)
        {
            var type = assemblies.Select(assembly => assembly.GetType(group.Key, false)).FirstOrDefault(type => type is not null);
            var methods = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly) ?? [];
            var attributes = methods.SelectMany(method => method.GetCustomAttributesData());
            var factMethods = methods.Count(method => method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name is "FactAttribute" or "Fact"));
            var theoryMethods = methods.Count(method => method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name is "TheoryAttribute" or "Theory"));
            var inlineDataRows = attributes.Count(attribute => attribute.AttributeType.Name is "InlineDataAttribute" or "InlineData");
            var identities = group.Value.OrderBy(value => value, StringComparer.Ordinal)
                .GroupBy(value => value, StringComparer.Ordinal)
                .SelectMany(grouping => grouping.Select((_, occurrence) => $"{grouping.Key}|occurrence={occurrence + 1}"))
                .ToArray();
            facts.Add(group.Key, new SpecUnitMigrationMtpFacts(factMethods, theoryMethods, inlineDataRows, identities.Length,
                SpecUnitMigrationInventory.Digest(identities), false, identities));
        }

        return new SpecUnitMigrationCompiledDiscovery(facts);
    }

    internal SpecUnitMigrationMtpFacts ForType(string fqn)
        => _facts.GetValueOrDefault(fqn) ?? new SpecUnitMigrationMtpFacts(0, 0, 0, 0, SpecUnitMigrationInventory.Digest([]), true, []);

    internal IEnumerable<(string Fqn, string Identity)> CaseIdentities
        => _facts.SelectMany(entry => entry.Value.CaseIdentities?.Select(identity => (entry.Key, identity)) ?? []);

    private sealed class InMemoryDiscoveryOptions : ITestFrameworkDiscoveryOptions
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        public TValue? GetValue<TValue>(string name) => _values.TryGetValue(name, out var value) ? (TValue?)value : default;
        public void SetValue<TValue>(string name, TValue value) => _values[name] = value;
        public string ToJson() => "{}";
    }

    // The top-level Find requires an active xUnit TestContext, which is unavailable during module warmup.
    private sealed class ScopedXunitTestFrameworkDiscoverer(
        IXunitTestAssembly testAssembly,
        IXunitTestCollectionFactory collectionFactory)
        : XunitTestFrameworkDiscoverer(testAssembly, collectionFactory)
    {
        internal async ValueTask FindTypes(
            Func<ITestCase, ValueTask<bool>> callback,
            ITestFrameworkDiscoveryOptions discoveryOptions,
            IReadOnlyList<Type> types)
        {
            foreach (var type in types)
            {
                if (type.IsAbstract && !type.IsSealed) continue;
                var testClass = new XunitTestClass(type, TestCollectionFactory.Get(type));
                if (!await FindTestsForType(testClass, discoveryOptions, callback)) return;
            }
        }
    }
}

internal sealed record SpecUnitMigrationMtpFacts(
    int FactMethods,
    int TheoryMethods,
    int InlineDataRows,
    int CaseCount,
    string CaseIdentityDigest,
    bool Missing,
    IReadOnlyList<string>? CaseIdentities = null);

internal sealed record SpecUnitMigrationExecutableFacts(
    string Fqn,
    string Path,
    int CaseCount,
    string CaseIdentityDigest,
    string ClosureIdentityDigest,
    string SourceContentDigest,
    string EdgesDigest,
    IReadOnlyList<string> CaseIdentities);

internal sealed record SpecUnitMigrationCandidate(
    string Fqn,
    string Path,
    int FactMethods,
    int TheoryMethods,
    int InlineDataRows,
    int ExecutableCaseCount,
    string ExecutableCaseIdentityDigest,
    string ClosureIdentityDigest,
    string SourceContentDigest,
    IReadOnlyList<string> ExecutableCaseIdentities,
    IReadOnlyList<string> Closure,
    IReadOnlyList<string> Blockers,
    string EdgesDigest)
{
    internal string Key => Fqn;
}
