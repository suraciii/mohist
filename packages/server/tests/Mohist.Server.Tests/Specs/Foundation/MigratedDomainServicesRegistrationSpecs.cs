using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Label.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services.Sessions;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Foundation;

[Collection("MohistDb")]
public class MigratedDomainServicesRegistrationSpecs
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private readonly MohistDbFixture _fixture;

    public MigratedDomainServicesRegistrationSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    public static IEnumerable<object[]> MigratedServices()
    {
        yield return new object[] { typeof(ProjectQuerier), ServiceLifetime.Singleton };
        yield return new object[] { typeof(ProjectRefResolver), ServiceLifetime.Singleton };
        yield return new object[] { typeof(IssueRepositoryResolver), ServiceLifetime.Singleton };
        yield return new object[] { typeof(SystemLabelDefinitions), ServiceLifetime.Singleton };
        yield return new object[] { typeof(IssueIdentityResolver), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(EpicQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(LabelCatalogService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueWorkflowProfileRegistry), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueTemplateRegistry), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentSessionQuery), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentSessionQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentSessionResolver), ServiceLifetime.Scoped };
    }

    public static IEnumerable<object[]> MigratedServicesResolvableThroughDbFixture()
    {
        var grainBlocked = new HashSet<Type>
        {
            typeof(AgentSessionResolver),
        };
        foreach (var row in MigratedServices())
        {
            var type = (Type)row[0];
            if (grainBlocked.Contains(type)) continue;
            yield return row;
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Theory]
    [MemberData(nameof(MigratedServices))]
    public void MigratedService_RegistersAsSelfWithOriginalLifetime_ThroughProductionEntry(
        Type serviceType, ServiceLifetime expectedLifetime)
    {
        var services = new ServiceCollection();
        services.AddMohistServerCore(EmptyConfig());

        var matching = services
            .Where(d => d.ServiceType == serviceType && d.ImplementationType == serviceType)
            .ToList();

        Assert.Single(matching);
        Assert.Equal(expectedLifetime, matching[0].Lifetime);

        var lastDescriptor = services.Last(d => d.ServiceType == serviceType);
        Assert.Equal(expectedLifetime, lastDescriptor.Lifetime);
        Assert.Equal(serviceType, lastDescriptor.ImplementationType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Theory]
    [MemberData(nameof(MigratedServicesResolvableThroughDbFixture))]
    public void MigratedService_IsResolvableAndRespectsLifetime_ThroughDbFixture(
        Type serviceType, ServiceLifetime expectedLifetime)
    {
        using var scope = _fixture.Services.CreateScope();

        var resolved = scope.ServiceProvider.GetService(serviceType);
        Assert.NotNull(resolved);
        Assert.Equal(serviceType, resolved!.GetType());

        if (expectedLifetime == ServiceLifetime.Singleton)
        {
            var fromSameScope = scope.ServiceProvider.GetService(serviceType);
            Assert.Same(resolved, fromSameScope);

            using var otherScope = _fixture.Services.CreateScope();
            var fromOtherScope = otherScope.ServiceProvider.GetService(serviceType);
            Assert.Same(resolved, fromOtherScope);
        }
        else
        {
            var fromSameScope = scope.ServiceProvider.GetService(serviceType);
            Assert.Same(resolved, fromSameScope);

            using var otherScope = _fixture.Services.CreateScope();
            var fromOtherScope = otherScope.ServiceProvider.GetService(serviceType);
            Assert.NotNull(fromOtherScope);
            Assert.NotSame(resolved, fromOtherScope);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Theory]
    [MemberData(nameof(MigratedServices))]
    public void MigratedService_FixtureAndProductionRegistrationAgreeOnTypeAndLifetime(
        Type serviceType, ServiceLifetime expectedLifetime)
    {
        var productionServices = new ServiceCollection();
        productionServices.AddMohistServerCore(EmptyConfig());

        var prodMatching = productionServices
            .Where(d => d.ServiceType == serviceType && d.ImplementationType == serviceType)
            .ToList();
        Assert.Single(prodMatching);
        var prodLast = productionServices.Last(d => d.ServiceType == serviceType);
        Assert.Equal(expectedLifetime, prodLast.Lifetime);
        Assert.Equal(serviceType, prodLast.ImplementationType);

        Assert.Equal(expectedLifetime, prodMatching[0].Lifetime);

        if (typeof(AgentSessionResolver).IsAssignableFrom(serviceType))
        {
            return;
        }

        using var fixtureScope = _fixture.Services.CreateScope();
        var fromFixture = fixtureScope.ServiceProvider.GetService(serviceType);
        Assert.NotNull(fromFixture);
        Assert.Equal(serviceType, fromFixture!.GetType());

        using var otherFixtureScope = _fixture.Services.CreateScope();
        var fromOtherFixture = otherFixtureScope.ServiceProvider.GetService(serviceType);
        Assert.NotNull(fromOtherFixture);

        if (expectedLifetime == ServiceLifetime.Singleton)
        {
            Assert.Same(fromFixture, fromOtherFixture);
        }
        else
        {
            Assert.NotSame(fromFixture, fromOtherFixture);
        }
    }
}