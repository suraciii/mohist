using System.Reflection;
using Xunit;
using Xunit.v3;

namespace Mohist.Server.TestSupport;

public static class TestLevelContract
{
    private const string LevelTrait = "level";

    public static void AssertAssembly(Assembly assembly)
    {
        var types = assembly.GetTypes();
        var testTypes = types
            .Where(IsConcreteTestType)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        var testTypeSet = testTypes.ToHashSet();
        var violations = new List<string>();

        foreach (var type in testTypes)
        {
            var traits = LevelTraits(type.GetCustomAttributesData()).ToArray();
            if (traits.Length != 1 || traits[0].Name != LevelTrait ||
                traits[0].Value is not ("L0" or "L1"))
            {
                var actual = traits.Length == 0
                    ? "none"
                    : string.Join(", ", traits.Select(trait => $"{trait.Name}={trait.Value}"));
                violations.Add($"{type.FullName}: expected exactly one direct level=L0 or level=L1 trait; found {actual}");
            }
        }

        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (!testTypeSet.Contains(type))
            {
                foreach (var trait in LevelTraits(type.GetCustomAttributesData()))
                    violations.Add($"{type.FullName}: {trait.Name}={trait.Value} is on a non-test or abstract type");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Instance | BindingFlags.Static |
                                                    BindingFlags.DeclaredOnly))
            {
                foreach (var trait in LevelTraits(method.GetCustomAttributesData()))
                    violations.Add($"{type.FullName}.{method.Name}: {trait.Name}={trait.Value} must be declared on the concrete test class");
            }
        }

        foreach (var trait in LevelTraits(assembly.GetCustomAttributesData()))
            violations.Add($"{assembly.GetName().Name}: {trait.Name}={trait.Value} must not be assembly-scoped");

        Assert.True(
            violations.Count == 0,
            $"{assembly.GetName().Name} test level contract violations:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    private static bool IsConcreteTestType(Type type) =>
        type.IsClass &&
        !type.IsAbstract &&
        (type.IsPublic || type.IsNestedPublic) &&
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Any(method => method.GetCustomAttributesData().Any(IsFactAttribute));

    private static bool IsFactAttribute(CustomAttributeData attribute) =>
        typeof(IFactAttribute).IsAssignableFrom(attribute.AttributeType);

    private static IEnumerable<(string? Name, string? Value)> LevelTraits(
        IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(attribute => attribute.AttributeType == typeof(TraitAttribute))
            .Select(attribute => (
                Name: attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string,
                Value: attribute.ConstructorArguments.ElementAtOrDefault(1).Value as string))
            .Where(trait => string.Equals(trait.Name, LevelTrait, StringComparison.OrdinalIgnoreCase));
}
