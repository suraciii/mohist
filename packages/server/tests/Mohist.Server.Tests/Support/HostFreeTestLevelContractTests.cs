using System.Reflection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests;

[Trait("level", "L0")]
public sealed class HostFreeTestLevelContractTests
{
    [Fact]
    public void Every_discovered_test_class_has_exactly_one_direct_level_trait() =>
        TestLevelContract.AssertAssembly(typeof(HostFreeTestLevelContractTests).Assembly);

    [Fact]
    public void Host_free_tests_do_not_request_the_application_host()
    {
        var assembly = typeof(HostFreeTestLevelContractTests).Assembly;
        var collectionFixtures = assembly.GetTypes()
            .Select(type => (Type: type, Name: AttributeValue(type, typeof(CollectionDefinitionAttribute))))
            .Where(item => item.Name is not null)
            .ToDictionary(
                item => item.Name!,
                item => FixtureTypes(item.Type, typeof(ICollectionFixture<>)).ToArray(),
                StringComparer.Ordinal);

        var violations = assembly.GetTypes()
            .Where(IsHostFreeTest)
            .SelectMany(type => RequestedFixtures(type, collectionFixtures)
                .Where(IsApplicationHost)
                .Select(fixture => $"{type.FullName}: requests {fixture.FullName}"))
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "level=L0 tests must not request the application host:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Host_free_guard_recognizes_wrapped_application_hosts() =>
        Assert.True(IsApplicationHost(typeof(WrappedApplicationHostFixture)));

    private static bool IsHostFreeTest(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute) &&
            attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string == "level" &&
            attribute.ConstructorArguments.ElementAtOrDefault(1).Value as string == "L0");

    private static IEnumerable<Type> RequestedFixtures(
        Type type,
        IReadOnlyDictionary<string, Type[]> collectionFixtures)
    {
        foreach (var fixture in FixtureTypes(type, typeof(IClassFixture<>)))
            yield return fixture;

        foreach (var fixture in type.GetConstructors().SelectMany(constructor =>
                     constructor.GetParameters().Select(parameter => parameter.ParameterType)))
            yield return fixture;

        var collection = AttributeValue(type, typeof(CollectionAttribute));
        if (collection is not null && collectionFixtures.TryGetValue(collection, out var fixtures))
        {
            foreach (var fixture in fixtures)
                yield return fixture;
        }
    }

    private static IEnumerable<Type> FixtureTypes(Type type, Type fixtureInterface) =>
        type.GetInterfaces()
            .Where(candidate => candidate.IsGenericType &&
                                candidate.GetGenericTypeDefinition() == fixtureInterface)
            .Select(candidate => candidate.GetGenericArguments()[0]);

    private static string? AttributeValue(Type type, Type attributeType) =>
        type.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == attributeType)
            .Select(attribute => attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string)
            .FirstOrDefault();

    private static bool IsApplicationHost(Type fixture) =>
        ContainsApplicationHost(fixture, []);

    private static bool ContainsApplicationHost(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
            return false;
        if (typeof(MohistIntegrationFixture).IsAssignableFrom(type)
            || type == typeof(DefaultApplicationHostOwner)
            || typeof(TestServer).IsAssignableFrom(type)
            || IsWebApplicationFactory(type))
            return true;
        if (type.Assembly != typeof(HostFreeTestLevelContractTests).Assembly
            && type.Assembly != typeof(TestLevelContract).Assembly)
            return false;

        return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   .Any(field => ContainsApplicationHost(field.FieldType, visited))
               || type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   .Any(property => ContainsApplicationHost(property.PropertyType, visited))
               || type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   .SelectMany(constructor => constructor.GetParameters())
                   .Any(parameter => ContainsApplicationHost(parameter.ParameterType, visited));
    }

    private static bool IsWebApplicationFactory(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType &&
                current.GetGenericTypeDefinition() == typeof(WebApplicationFactory<>))
                return true;
        }

        return false;
    }

    private sealed class WrappedApplicationHostFixture
    {
        public WebApplicationFactory<Program>? Factory { get; }
    }
}
