using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Xunit.Sdk;

namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationCompiledDiscovery
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    private readonly IReadOnlyDictionary<string, SpecUnitMigrationMtpFacts> _facts;
    private readonly Assembly[] _assemblies;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _runtimeReferences = new(StringComparer.Ordinal);

    private SpecUnitMigrationCompiledDiscovery(
        IReadOnlyDictionary<string, SpecUnitMigrationMtpFacts> facts,
        params Assembly[] assemblies)
    {
        _facts = facts;
        _assemblies = assemblies;
    }

    internal static SpecUnitMigrationCompiledDiscovery Empty { get; } = new(new Dictionary<string, SpecUnitMigrationMtpFacts>(StringComparer.Ordinal));

    internal static SpecUnitMigrationCompiledDiscovery ForTests(params (string Fqn, SpecUnitMigrationMtpFacts Facts)[] entries)
        => new(entries.ToDictionary(entry => entry.Fqn, entry => entry.Facts, StringComparer.Ordinal));

    internal static SpecUnitMigrationCompiledDiscovery FromAssemblies(
        IEnumerable<string> requestedFqns,
        params Assembly[] assemblies)
    {
        var requested = requestedFqns.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var cases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unsupportedTheoryData = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var types = requested.Select(fqn => assembly.GetType(fqn, throwOnError: false))
                .Where(type => type is not null).Cast<Type>().ToArray();
            if (types.Length == 0) continue;
            foreach (var type in types)
            {
                var fqn = type.FullName!;
                var identities = cases.GetValueOrDefault(fqn);
                if (identities is null) cases.Add(fqn, identities = []);
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var attributes = method.GetCustomAttributesData();
                    if (attributes.Any(attribute => AttributeNameIs(attribute, "Fact")))
                        identities.Add(string.Join("|", fqn, method.Name, $"{fqn}.{method.Name}"));
                    if (!attributes.Any(attribute => AttributeNameIs(attribute, "Theory"))) continue;
                    var dataAttributes = attributes.Where(attribute => typeof(Xunit.v3.DataAttribute)
                        .IsAssignableFrom(attribute.AttributeType)).ToArray();
                    if (dataAttributes.Length == 0 || dataAttributes.Any(attribute => !AttributeNameIs(attribute, "InlineData")))
                    {
                        unsupportedTheoryData.Add(fqn);
                        continue;
                    }
                    foreach (var inlineData in dataAttributes)
                    {
                        var values = InlineDataValues(inlineData);
                        var parameters = method.GetParameters();
                        if (values.Length != parameters.Length)
                        {
                            unsupportedTheoryData.Add(fqn);
                            continue;
                        }
                        var arguments = parameters.Select((parameter, index) =>
                            $"{parameter.Name}: {ArgumentFormatter.Format(values[index], 1)}");
                        identities.Add(string.Join("|", fqn, method.Name,
                            $"{fqn}.{method.Name}({string.Join(", ", arguments)})"));
                    }
                }
            }
        }

        var facts = new Dictionary<string, SpecUnitMigrationMtpFacts>(StringComparer.Ordinal);
        foreach (var group in cases)
        {
            var type = assemblies.Select(assembly => assembly.GetType(group.Key, false)).FirstOrDefault(value => value is not null);
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
                SpecUnitMigrationInventory.Digest(identities), unsupportedTheoryData.Contains(group.Key), identities));
        }

        return new SpecUnitMigrationCompiledDiscovery(facts, assemblies);
    }

    internal SpecUnitMigrationMtpFacts ForType(string fqn)
        => _facts.GetValueOrDefault(fqn) ?? new SpecUnitMigrationMtpFacts(0, 0, 0, 0, SpecUnitMigrationInventory.Digest([]), true, []);

    internal IReadOnlyCollection<string> Fqns => _facts.Keys.ToArray();

    internal bool HasRuntimeReferences => _assemblies.Length > 0;

    internal IReadOnlyList<string> RuntimeReferencesFor(string fqn)
        => _runtimeReferences.GetOrAdd(fqn, DiscoverRuntimeReferences);

    internal IEnumerable<(string Fqn, string Identity)> CaseIdentities
        => _facts.SelectMany(entry => entry.Value.CaseIdentities?.Select(identity => (entry.Key, identity)) ?? []);

    private static object?[] InlineDataValues(CustomAttributeData attribute)
    {
        if (attribute.ConstructorArguments.Count != 1) return [];
        var values = attribute.ConstructorArguments[0].Value as IReadOnlyCollection<CustomAttributeTypedArgument>;
        return values?.Select(Value).ToArray() ?? [];
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

    private IReadOnlyList<string> DiscoverRuntimeReferences(string fqn)
    {
        var root = _assemblies.Select(assembly => assembly.GetType(fqn, throwOnError: false))
            .FirstOrDefault(type => type is not null);
        if (root is null) return [];

        var references = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Type>([root]);
        var visited = new HashSet<Type>();
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!visited.Add(type)) continue;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) pending.Push(nested);
            AddTypeReference(type.BaseType, references);
            foreach (var interfaceType in type.GetInterfaces()) AddTypeReference(interfaceType, references);
            AddAttributeReferences(type.GetCustomAttributesData(), references);

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AddTypeReference(field.FieldType, references);
                AddAttributeReferences(field.GetCustomAttributesData(), references);
            }
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AddTypeReference(property.PropertyType, references);
                foreach (var parameter in property.GetIndexParameters()) AddTypeReference(parameter.ParameterType, references);
                AddAttributeReferences(property.GetCustomAttributesData(), references);
            }
            foreach (var eventInfo in type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AddTypeReference(eventInfo.EventHandlerType, references);
                AddAttributeReferences(eventInfo.GetCustomAttributesData(), references);
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
            foreach (var method in methods) AddMethodReferences(method, references);
        }

        references.Remove(fqn);
        return references.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static void AddMethodReferences(MethodBase method, ISet<string> references)
    {
        if (method is MethodInfo methodInfo) AddTypeReference(methodInfo.ReturnType, references);
        foreach (var parameter in method.GetParameters()) AddTypeReference(parameter.ParameterType, references);
        if (method is MethodInfo genericMethod)
            foreach (var argument in genericMethod.GetGenericArguments())
                foreach (var constraint in argument.GetGenericParameterConstraints()) AddTypeReference(constraint, references);
        AddAttributeReferences(method.GetCustomAttributesData(), references);

        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (body is null) return;
        foreach (var local in body.LocalVariables) AddTypeReference(local.LocalType, references);
        foreach (var clause in body.ExceptionHandlingClauses)
            if (clause.Flags == ExceptionHandlingClauseOptions.Clause) AddTypeReference(clause.CatchType, references);
        AddIlReferences(method, body.GetILAsByteArray() ?? [], references);
    }

    private static void AddIlReferences(MethodBase method, byte[] il, ISet<string> references)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var value = (ushort)il[offset++];
            if (value == 0xfe && offset < il.Length) value = (ushort)(0xfe00 | il[offset++]);
            if (!OpCodesByValue.TryGetValue(value, out var opCode)) return;
            if (opCode.OperandType is OperandType.InlineField or OperandType.InlineMethod
                or OperandType.InlineTok or OperandType.InlineType)
            {
                if (offset + sizeof(int) > il.Length) return;
                var token = BitConverter.ToInt32(il, offset);
                try
                {
                    var methodArguments = method is MethodInfo methodInfo && methodInfo.IsGenericMethod
                        ? methodInfo.GetGenericArguments()
                        : null;
                    var member = method.Module.ResolveMember(token,
                        method.DeclaringType?.GetGenericArguments(), methodArguments);
                    AddMemberReference(member, references);
                }
                catch (ArgumentException)
                {
                    // Unresolved IL is not treated as proof; the caller falls back to Roslyn when no blocker is found.
                }
            }

            var operandSize = OperandSize(opCode.OperandType, il, offset);
            if (operandSize < 0 || offset + operandSize > il.Length) return;
            offset += operandSize;
        }
    }

    private static int OperandSize(OperandType operandType, byte[] il, int offset)
        => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => InlineSwitchOperandSize(il, offset),
            _ => -1,
        };

    private static int InlineSwitchOperandSize(byte[] il, int offset)
    {
        if (offset < 0 || offset > il.Length - sizeof(int)) return -1;
        var count = BitConverter.ToInt32(il, offset);
        if (count < 0 || count > (il.Length - offset - sizeof(int)) / sizeof(int)) return -1;
        return sizeof(int) + count * sizeof(int);
    }

    private static void AddMemberReference(MemberInfo? member, ISet<string> references)
    {
        if (member is null) return;
        AddTypeReference(member as Type ?? member.DeclaringType, references);
        switch (member)
        {
            case FieldInfo field:
                AddTypeReference(field.FieldType, references);
                break;
            case MethodInfo method:
                AddTypeReference(method.ReturnType, references);
                foreach (var parameter in method.GetParameters()) AddTypeReference(parameter.ParameterType, references);
                break;
        }
    }

    private static void AddAttributeReferences(IEnumerable<CustomAttributeData> attributes, ISet<string> references)
    {
        foreach (var attribute in attributes)
        {
            AddTypeReference(attribute.AttributeType, references);
            foreach (var argument in attribute.ConstructorArguments) AddAttributeArgumentReference(argument, references);
            foreach (var argument in attribute.NamedArguments) AddAttributeArgumentReference(argument.TypedValue, references);
        }
    }

    private static void AddAttributeArgumentReference(CustomAttributeTypedArgument argument, ISet<string> references)
    {
        AddTypeReference(argument.ArgumentType, references);
        if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> values)
            foreach (var value in values) AddAttributeArgumentReference(value, references);
        else if (argument.Value is Type type)
            AddTypeReference(type, references);
    }

    private static void AddTypeReference(Type? type, ISet<string> references)
    {
        if (type is null) return;
        while (type.HasElementType) type = type.GetElementType()!;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments()) AddTypeReference(argument, references);
            type = type.GetGenericTypeDefinition();
        }
        for (var candidate = type; candidate is not null; candidate = candidate.DeclaringType)
        {
            var fqn = candidate.FullName?.Replace('+', '.');
            if (!string.IsNullOrWhiteSpace(fqn)) references.Add(fqn);
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
