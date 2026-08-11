using System.Reflection;
using Xunit.Sdk;

namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationCompiledDiscovery
{
    private readonly IReadOnlyDictionary<string, SpecUnitMigrationMtpFacts> _facts;

    private SpecUnitMigrationCompiledDiscovery(
        IReadOnlyDictionary<string, SpecUnitMigrationMtpFacts> facts,
        string bindingIdentity)
    {
        _facts = facts;
        BindingIdentity = bindingIdentity;
    }

    internal static SpecUnitMigrationCompiledDiscovery Empty { get; } = new(
        new Dictionary<string, SpecUnitMigrationMtpFacts>(StringComparer.Ordinal),
        SpecUnitMigrationInventory.Digest(["empty"]));

    internal static SpecUnitMigrationCompiledDiscovery ForTests(
        params (string Fqn, SpecUnitMigrationMtpFacts Facts)[] entries)
        => Create(entries, SpecUnitMigrationInventory.Digest(entries.Select(entry =>
            $"test|{entry.Fqn}|{entry.Facts.CaseIdentityDigest}|{entry.Facts.Missing}")));

    internal static SpecUnitMigrationCompiledDiscovery ForSource(
        IEnumerable<(string Fqn, SpecUnitMigrationMtpFacts Facts)> entries,
        string sourceIdentity)
        => Create(entries, SpecUnitMigrationInventory.Digest(["source", sourceIdentity]));

    private static SpecUnitMigrationCompiledDiscovery Create(
        IEnumerable<(string Fqn, SpecUnitMigrationMtpFacts Facts)> entries,
        string bindingIdentity)
        => new(entries.ToDictionary(entry => entry.Fqn, entry => entry.Facts, StringComparer.Ordinal), bindingIdentity);

    internal static SpecUnitMigrationCompiledDiscovery FromAssemblies(
        IEnumerable<string> requestedFqns,
        params Assembly[] assemblies)
    {
        var requested = requestedFqns.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var facts = requested.Select(fqn => DiscoverAssemblyType(fqn, assemblies))
            .Where(entry => entry is not null).Cast<(string Fqn, SpecUnitMigrationMtpFacts Facts)>().ToArray();
        var bindingIdentity = SpecUnitMigrationInventory.Digest(
        [
            .. requested.Select(fqn => $"type|{fqn}"),
            .. assemblies.Select(assembly =>
                $"assembly|{assembly.GetName().FullName}|{assembly.ManifestModule.ModuleVersionId}"),
        ]);
        return Create(facts, bindingIdentity);
    }

    internal SpecUnitMigrationMtpFacts ForType(string fqn)
        => _facts.GetValueOrDefault(fqn)
            ?? new SpecUnitMigrationMtpFacts(0, 0, 0, 0, SpecUnitMigrationInventory.Digest([]), true, []);

    internal IReadOnlyCollection<string> Fqns => _facts.Keys.ToArray();
    internal string BindingIdentity { get; }

    internal IEnumerable<(string Fqn, string Identity)> CaseIdentities
        => _facts.SelectMany(entry => entry.Value.CaseIdentities?.Select(identity => (entry.Key, identity)) ?? []);

    private static (string Fqn, SpecUnitMigrationMtpFacts Facts)? DiscoverAssemblyType(
        string fqn,
        IReadOnlyList<Assembly> assemblies)
    {
        var type = assemblies.Select(assembly => assembly.GetType(fqn, throwOnError: false))
            .FirstOrDefault(value => value is not null);
        if (type is null) return null;

        var identities = new List<string>();
        var factMethods = 0;
        var theoryMethods = 0;
        var inlineDataRows = 0;
        var incomplete = false;
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var attributes = method.GetCustomAttributesData();
            if (attributes.Any(attribute => AttributeNameIs(attribute, "Fact")))
            {
                factMethods++;
                identities.Add(string.Join("|", fqn, method.Name, $"{fqn}.{method.Name}"));
            }

            if (!attributes.Any(attribute => AttributeNameIs(attribute, "Theory"))) continue;
            theoryMethods++;
            var dataAttributes = attributes.Where(attribute => typeof(Xunit.v3.DataAttribute)
                .IsAssignableFrom(attribute.AttributeType)).ToArray();
            if (dataAttributes.Length == 0 || dataAttributes.Any(attribute => !AttributeNameIs(attribute, "InlineData")))
            {
                incomplete = true;
                continue;
            }

            foreach (var inlineData in dataAttributes)
            {
                if (!TryInlineDataValues(inlineData, out var values) || values.Length != method.GetParameters().Length)
                {
                    incomplete = true;
                    continue;
                }
                inlineDataRows++;
                var arguments = method.GetParameters().Select((parameter, index) =>
                    $"{parameter.Name}: {ArgumentFormatter.Format(values[index], 1)}");
                identities.Add(string.Join("|", fqn, method.Name,
                    $"{fqn}.{method.Name}({string.Join(", ", arguments)})"));
            }
        }

        var distinctIdentities = identities.OrderBy(value => value, StringComparer.Ordinal)
            .GroupBy(value => value, StringComparer.Ordinal)
            .SelectMany(group => group.Select((_, occurrence) => $"{group.Key}|occurrence={occurrence + 1}"))
            .ToArray();
        return (fqn, new SpecUnitMigrationMtpFacts(factMethods, theoryMethods, inlineDataRows,
            distinctIdentities.Length, SpecUnitMigrationInventory.Digest(distinctIdentities),
            incomplete || factMethods + theoryMethods == 0, distinctIdentities));
    }

    private static bool TryInlineDataValues(CustomAttributeData attribute, out object?[] values)
    {
        values = [];
        if (attribute.ConstructorArguments.Count != 1
            || attribute.ConstructorArguments[0].Value is not IReadOnlyCollection<CustomAttributeTypedArgument> arguments)
            return false;
        values = arguments.Select(Value).ToArray();
        return true;
    }

    private static object? Value(CustomAttributeTypedArgument argument)
    {
        if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> values)
            return values.Select(Value).ToArray();
        if (argument.ArgumentType.IsEnum && argument.Value is not null)
            return Enum.ToObject(argument.ArgumentType, argument.Value);
        return argument.Value;
    }

    private static bool AttributeNameIs(CustomAttributeData attribute, string expected)
        => attribute.AttributeType.Name is var name && (name == expected || name == expected + "Attribute");
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
