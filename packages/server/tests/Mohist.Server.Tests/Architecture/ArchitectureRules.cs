using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Mohist.Server.Infrastructure.Data.Db;
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

    private static readonly IObjectProvider<IType> ApplicationLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.(Grains|Services)", useRegularExpressions: true)
        .And().DoNotResideInNamespace("Mohist.Server.Infrastructure", useRegularExpressions: true)
        .As("Application Layer");

    private static readonly IObjectProvider<IType> GrainInterfaces = Interfaces()
        .That().ResideInNamespace("Mohist.Server.*.Grains", useRegularExpressions: true)
        .As("Grain Interfaces");

    private static readonly IObjectProvider<IType> DataLayer = Types()
        .That().ResideInNamespace("Mohist.Server.Infrastructure.Data", useRegularExpressions: true)
        .As("Data Layer");

    private static readonly IObjectProvider<IType> QuerierLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Services", useRegularExpressions: true)
        .And().HaveNameEndingWith("Querier")
        .As("Querier Layer");

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
            .Should().NotDependOnAny(DataLayer)
            .Because("Domain layer must not depend on database implementation")
            .Check(_architecture);
    }

    [Fact]
    public void Api_ShouldNotDependOnStorage()
    {
        Types().That().Are(ApiLayer)
            .Should().NotDependOnAny(DataLayer)
            .Because("API layer should use grain for writes and query services for reads")
            .Check(_architecture);
    }

    [Fact]
    public void Queriers_ShouldNotDependOnGrainInterfaces()
    {
        Types().That().Are(QuerierLayer)
            .Should().NotDependOnAny(GrainInterfaces)
            .Because("queriers should read from EF directly, not through grain interfaces")
            .Check(_architecture);
    }

    [Fact]
    public void InfrastructureData_ShouldNotDependOnApplicationLayer()
    {
        Types().That().Are(DataLayer)
            .Should().NotDependOnAny(ApplicationLayer)
            .Because("Infrastructure.Data is the persistence boundary and must not depend on application services, grains, or queriers")
            .Check(_architecture);
    }

    [Fact]
    public void DataStores_AreInInfrastructureData()
    {
        Classes().That().HaveNameEndingWith("Store")
            .And().ResideInNamespace("Mohist.Server.Infrastructure.Data", useRegularExpressions: true)
            .Should().Exist()
            .Because("database-backed stores should be in Infrastructure.Data namespace")
            .Check(_architecture);
    }

    [Fact]
    public void RowModels_AreInInfrastructureData()
    {
        Classes().That().HaveNameEndingWith("Row")
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data(\\..*)?", useRegularExpressions: true)
            .Because("EF row models are persistence data models and belong under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void DbContexts_AreInInfrastructureData()
    {
        Classes().That().AreAssignableTo(typeof(Microsoft.EntityFrameworkCore.DbContext))
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data(\\..*)?", useRegularExpressions: true)
            .Because("database contexts are infrastructure data concerns")
            .Check(_architecture);
    }

    [Fact]
    public void Migrations_AreInInfrastructureData()
    {
        Classes().That().AreAssignableTo(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration))
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data.Migrations", useRegularExpressions: true)
            .Because("EF migrations should live with database schema artifacts under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void ModelSnapshots_AreInInfrastructureData()
    {
        Classes().That().HaveNameEndingWith("ModelSnapshot")
            .Should().ResideInNamespace("Mohist.Server.Infrastructure.Data.Migrations", useRegularExpressions: true)
            .Because("EF model snapshots should live with database schema artifacts under Infrastructure.Data")
            .Check(_architecture);
    }

    [Fact]
    public void FeatureDirectories_ShouldOnlyContainDomainGrainsAndServices()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "Mohist.Server"));

        var sourceFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToArray();

        var featureRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "Epic",
            "Issue",
            "Project",
            "Runner",
            "Sessions",
            "Workflow"
        };

        var allowedFeatureSegments = new HashSet<string>(StringComparer.Ordinal)
        {
            "Domain",
            "Grains",
            "Services"
        };

        var violations = sourceFiles
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .Select(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(parts => parts.Length >= 2 && featureRoots.Contains(parts[0]))
            .Where(parts => !allowedFeatureSegments.Contains(parts[1]))
            .Select(parts => string.Join("/", parts))
            .OrderBy(path => path)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Feature directories must only contain Domain, Grains, and Services. Violations: " + string.Join(", ", violations));
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
        var dbSetProperties = typeof(Mohist.Server.Infrastructure.Data.Db.MohistDbContext)
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

    [Fact(Skip = "Tech debt: Issue has internal cycles (Grains↔Services)")]
    public void IssueInternalLayers_ShouldBeFreeOfCycles()
    {
        Slices().Matching("Mohist.Server.Issue.(*)")
            .Should().BeFreeOfCycles()
            .Check(_architecture);
    }

    [Fact(Skip = "Tech debt: Workflow has internal cycles (Grains↔Services)")]
    public void WorkflowInternalLayers_ShouldBeFreeOfCycles()
    {
        Slices().Matching("Mohist.Server.Workflow.(*)")
            .Should().BeFreeOfCycles()
            .Check(_architecture);
    }
}
