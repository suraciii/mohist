using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Foundation;

[Collection("MohistDb")]
public class MigratedRunnerSystemArtifactServicesRegistrationSpecs
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private readonly MohistDbFixture _fixture;

    public MigratedRunnerSystemArtifactServicesRegistrationSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    public static IEnumerable<object[]> MigratedRunnerSystemArtifactServices()
    {
        yield return new object[] { typeof(ConnectionSubscriptionRegistry), ServiceLifetime.Singleton };
        yield return new object[] { typeof(ConfigService), ServiceLifetime.Singleton };
        yield return new object[] { typeof(RuntimeBuildInfo), ServiceLifetime.Singleton };
        yield return new object[] { typeof(SystemdInstallDetector), ServiceLifetime.Singleton };
        yield return new object[] { typeof(SystemInfoService), ServiceLifetime.Singleton };
        yield return new object[] { typeof(SystemUpdateService), ServiceLifetime.Singleton };
        yield return new object[] { typeof(RunnerConnectionTracker), ServiceLifetime.Singleton };
        yield return new object[] { typeof(AttachmentService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactUploadService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(Mohist.Server.Workflow.Services.Artifacts.AgentJobArtifactUploadService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(Mohist.Server.Runner.Services.RunnerStatusService), ServiceLifetime.Scoped };
    }

    public static IEnumerable<object[]> MigratedRunnerSystemArtifactServicesResolvableThroughDbFixture()
    {
        var grainBlocked = new HashSet<Type>
        {
            typeof(Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactUploadService),
            typeof(Mohist.Server.Workflow.Services.Artifacts.AgentJobArtifactUploadService),
            typeof(Mohist.Server.Runner.Services.RunnerStatusService),
        };
        foreach (var row in MigratedRunnerSystemArtifactServices())
        {
            var type = (Type)row[0];
            if (grainBlocked.Contains(type)) continue;
            yield return row;
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Theory]
    [MemberData(nameof(MigratedRunnerSystemArtifactServices))]
    public void MigratedRunnerSystemArtifactService_RegistersAsSelfWithOriginalLifetime_ThroughProductionEntry(
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
    [MemberData(nameof(MigratedRunnerSystemArtifactServicesResolvableThroughDbFixture))]
    public void MigratedRunnerSystemArtifactService_IsResolvableAndRespectsLifetime_ThroughDbFixture(
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
    [MemberData(nameof(MigratedRunnerSystemArtifactServices))]
    public void MigratedRunnerSystemArtifactService_FixtureAndProductionRegistrationAgreeOnTypeAndLifetime(
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

        var grainBlocked = new HashSet<Type>
        {
            typeof(Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactUploadService),
            typeof(Mohist.Server.Workflow.Services.Artifacts.AgentJobArtifactUploadService),
            typeof(Mohist.Server.Runner.Services.RunnerStatusService),
        };
        if (grainBlocked.Contains(serviceType))
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
