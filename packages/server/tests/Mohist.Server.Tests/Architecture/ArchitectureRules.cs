using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Mohist.Server.Storage.Db;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

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
        .And().DoNotResideInNamespace("Mohist.Server.*.GrainStorage", useRegularExpressions: true)
        .Or().ResideInNamespace("Mohist.Server.Storage")
        .As("Storage Layer");

    private static readonly IObjectProvider<IType> GrainStorageLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.GrainStorage", useRegularExpressions: true)
        .As("GrainStorage Layer");

    private static readonly IObjectProvider<IType> QueryLayer = Types()
        .That().ResideInNamespace("Mohist.Server.*.Queries", useRegularExpressions: true)
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
    public void Queries_ShouldNotDependOnGrainInterfaces()
    {
        Types().That().Are(QueryLayer)
            .Should().NotDependOnAny(GrainInterfaces)
            .Because("Query layer should read from EF directly, not through grain interfaces")
            .Check(_architecture);
    }

    [Fact]
    public void Queries_ShouldNotDependOnGrainStorage()
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
    public void GrainStorage_IsSeparatedFromStorage()
    {
        Classes().That().HaveNameEndingWith("Store")
            .And().ResideInNamespace("Mohist.Server.*.GrainStorage", useRegularExpressions: true)
            .Should().Exist()
            .Because("Grain storage implementations should be in GrainStorage namespace")
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
        var dbSetProperties = typeof(Mohist.Server.Storage.Db.MohistDbContext)
            .GetProperties()
            .Where(p => p.PropertyType.IsGenericType &&
                        p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .ToList();

        Assert.All(dbSetProperties, entityType =>
        {
            Assert.True(entityType.Name.EndsWith("Row"),
                $"EF entity '{entityType.Name}' must end with 'Row'. " +
                $"Entity type: {entityType.FullName}");
        });
    }
}
