using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Mohist.Server.Infrastructure.Persistence.Db;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using static ArchUnitNET.Fluent.Slices.SliceRuleDefinition;

namespace Mohist.Server.Tests.Architecture;

public class ArchitectureRules
{
    private static readonly ArchUnitNET.Domain.Architecture _architecture = new ArchLoader()
        .LoadAssemblies(System.Reflection.Assembly.Load("Mohist.Server"))
        .Build();

    private static readonly IObjectProvider<IType> OrleansGeneratedTypes = Types()
        .That().ResideInNamespace("OrleansCodeGen", true)
        .As("Orleans Generated Types");

    // Layer definitions
    private static readonly IObjectProvider<IType> DomainLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Domain", useRegularExpressions: true)
        .As("Domain Layer");

    private static readonly IObjectProvider<IType> ApiLayer = Types()
        .That().ResideInNamespace("Mohist.Server.Api")
        .As("API Layer");

    private static readonly IObjectProvider<IType> GrainLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Grains", useRegularExpressions: true)
        .As("Grain Layer");

    private static readonly IObjectProvider<IType> GrainInterfaces = Interfaces()
        .That().ResideInNamespace("Mohist.Server.*.Grains", useRegularExpressions: true)
        .As("Grain Interfaces");

    private static readonly IObjectProvider<IType> StorageLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Storage", useRegularExpressions: true)
        .And().DoNotResideInNamespace("Mohist.Server.Infrastructure.Persistence", useRegularExpressions: true)
        .As("Storage Layer");

    private static readonly IObjectProvider<IType> GrainStorageLayer = Types()
        .That().ResideInNamespace("Mohist.Server.Infrastructure.Persistence", useRegularExpressions: true)
        .And().HaveNameEndingWith("Store")
        .As("GrainStorage Layer");

    private static readonly IObjectProvider<IType> QueryLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Querying", useRegularExpressions: true)
        .As("Query Layer");

    private static readonly IObjectProvider<IType> OrleansTypes = Types()
        .That().ResideInNamespace("Orleans")
        .As("Orleans Types");

    // Rules

    [Fact]
    public void Domain_ShouldNotDependOnOrleans()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(OrleansTypes)
            .Because("Domain layer must be independent of Orleans infrastructure")
            .Check(_architecture);
    }

    [Fact]
    public void Domain_ShouldNotDependOnStorage()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(StorageLayer)
            .Because("Domain layer must not depend on storage implementation")
            .Check(_architecture);
    }

    [Fact]
    public void Api_ShouldNotDependOnStorage()
    {
        Types().That().Are(ApiLayer)
            .Should().NotDependOnAny(Types().That().Are(StorageLayer).Or().Are(GrainStorageLayer))
            .Because("API layer should use grain for writes and query services for reads")
            .Check(_architecture);
    }

    [Fact]
    public void Querying_ShouldNotDependOnGrainInterfaces()
    {
        Types().That().Are(QueryLayer)
            .Should().NotDependOnAny(GrainInterfaces)
            .Because("Query layer should read from EF directly, not through grain interfaces")
            .Check(_architecture);
    }

    [Fact]
    public void Querying_ShouldNotDependOnGrainStorage()
    {
        Types().That().Are(QueryLayer)
            .Should().NotDependOnAny(GrainStorageLayer)
            .Because("Query layer should not depend on grain storage implementations")
            .Check(_architecture);
    }

    [Fact]
    public void Storage_ShouldNotDependOnGrainInterfaces()
    {
        Types().That().Are(StorageLayer)
            .Should().NotDependOnAny(GrainInterfaces)
            .Because("Storage layer should not depend on grain interfaces")
            .Check(_architecture);
    }

    [Fact]
    public void GrainStorage_IsInInfrastructurePersistence()
    {
        Classes().That().HaveNameEndingWith("Store")
            .And().ResideInNamespace("Mohist.Server.Infrastructure.Persistence", useRegularExpressions: true)
            .Should().Exist()
            .Because("Grain storage implementations should be in Infrastructure.Persistence namespace")
            .Check(_architecture);
    }

    [Fact]
    public void GrainImplementations_ShouldInheritFromGrain()
    {
        Classes().That().HaveNameEndingWith("Grain")
            .And().DoNotHaveNameStartingWith("I")
            .And().AreNot(OrleansGeneratedTypes)
            .And().DoNotResideInNamespace("OrleansCodeGen", true)
            .Should().BeAssignableTo(typeof(Orleans.Grain))
            .Because("Grain implementations must inherit from Orleans.Grain")
            .Check(_architecture);
    }

    [Fact]
    public void GrainInterfaces_ShouldStartWithI()
    {
        Interfaces().That().HaveNameEndingWith("Grain")
            .Should().HaveNameStartingWith("I")
            .Because("Grain interfaces should follow naming convention")
            .Check(_architecture);
    }

    [Fact]
    public void Domain_ShouldNotDependOnApi()
    {
        Types().That().Are(DomainLayer)
            .Should().NotDependOnAny(ApiLayer)
            .Because("Domain layer must not depend on API layer")
            .Check(_architecture);
    }

    [Fact]
    public void GrainInterfaces_ShouldBeInGrainsNamespace()
    {
        Interfaces().That().HaveNameEndingWith("Grain")
            .Should().ResideInNamespace("Mohist.Server.*.Grains", useRegularExpressions: true)
            .Because("Grain interfaces must be in Grains namespace")
            .Check(_architecture);
    }

    [Fact]
    public void Api_ShouldNotDependOnOrleans()
    {
        Types().That().Are(ApiLayer)
            .Should().NotDependOnAny(OrleansTypes)
            .Because("API layer should not depend on Orleans directly")
            .Check(_architecture);
    }

    [Fact]
    public void EfEntities_ShouldEndWithRow()
    {
        var dbSetProperties = typeof(Mohist.Server.Infrastructure.Persistence.Db.MohistDbContext)
            .GetProperties()
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .ToList();

        Assert.All(dbSetProperties, entityType =>
        {
            Assert.True(
                entityType.Name.EndsWith("Row") || entityType.Name.EndsWith("Profile"),
                $"EF entity '{entityType.Name}' must end with 'Row' or 'Profile'. " +
                $"Entity type: {entityType.FullName}");
        });
    }

    private static readonly string[] DomainNamespaces =
        ["Issue", "Workflow", "Epic", "Project", "Runner", "Sessions"];

    private static readonly (string from, string to)[] AllowedDomainDependencies =
    [
        ("Issue", "Workflow"),
        ("Issue", "Epic"),
        ("Issue", "Project"),
        ("Runner", "Sessions"),
        ("Runner", "Workflow"),
        ("Workflow", "Sessions"),
    ];

    [Fact]
    public void DomainModules_ShouldNotDependOnEachOther()
    {
        for (int i = 0; i < DomainNamespaces.Length; i++)
        {
            for (int j = i + 1; j < DomainNamespaces.Length; j++)
            {
                var a = DomainNamespaces[i];
                var b = DomainNamespaces[j];

                if (AllowedDomainDependencies.Contains((a, b)) ||
                    AllowedDomainDependencies.Contains((b, a)))
                    continue;

                var aTypes = Types()
                    .That().ResideInNamespace($"Mohist.Server.{a}", useRegularExpressions: true)
                    .And().DoNotResideInNamespace("OrleansCodeGen", true)
                    .As($"{a}");

                var bTypes = Types()
                    .That().ResideInNamespace($"Mohist.Server.{b}", useRegularExpressions: true)
                    .And().DoNotResideInNamespace("OrleansCodeGen", true)
                    .As($"{b}");

                Types().That().Are(aTypes)
                    .Should().NotDependOnAny(bTypes)
                    .Check(_architecture);
            }
        }
    }

    [Fact]
    public void DomainInternalLayers_ShouldBeFreeOfCycles()
    {
        var domainsWithKnownCycles = new HashSet<string> { "Issue", "Workflow" };

        foreach (var domain in DomainNamespaces)
        {
            if (domainsWithKnownCycles.Contains(domain))
                continue;

            Slices().Matching($"Mohist.Server.{domain}.(*)")
                .Should().BeFreeOfCycles()
                .Check(_architecture);
        }
    }

    [Fact(Skip = "Tech debt: Issue has internal cycles (Storage↔WorkflowProfiles↔Querying, Grains↔WorkflowProfiles)")]
    public void IssueInternalLayers_ShouldBeFreeOfCycles()
    {
        Slices().Matching("Mohist.Server.Issue.(*)")
            .Should().BeFreeOfCycles()
            .Check(_architecture);
    }

    [Fact(Skip = "Tech debt: Workflow has internal cycles (Storage↔WorkflowProfiles↔Querying, Grains↔WorkflowProfiles)")]
    public void WorkflowInternalLayers_ShouldBeFreeOfCycles()
    {
        Slices().Matching("Mohist.Server.Workflow.(*)")
            .Should().BeFreeOfCycles()
            .Check(_architecture);
    }
}
