using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Label.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SystemInfo;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Foundation;

/// <summary>
/// Verifies that services migrated off the legacy registry are still
/// registered through <see cref="MohistServiceRegistration"/> /
/// <c>AddMohistServerCore</c> with their original lifetime, resolve
/// correctly, and agree between the production service graph and the
/// <see cref="MohistDbFixture"/> graph.
/// </summary>
/// <remarks>
/// Consolidates the former per-domain MigratedDomainServices /
/// MigratedWorkflowServices / MigratedRunnerSystemArtifactServices spec
/// files, which shared an identical three-theory structure and differed
/// only in their service lists. Grain-backed services (those that need an
/// Orleans silo to resolve) are listed in <see cref="GrainBlocked"/> and
/// are covered for resolution by
/// <c>GrainBackedMigratedServicesRegistrationSpecs</c>.
/// </remarks>
[Collection("MohistDb")]
public class MigratedServicesRegistrationSpecs
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private readonly MohistDbFixture _fixture;

    public MigratedServicesRegistrationSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Services that require a live Orleans silo and therefore cannot be
    /// resolved through <see cref="MohistDbFixture"/> (which has no silo).
    /// Their production registration is still asserted; their fixture-side
    /// resolution is covered by the grain-backed specs.
    /// </summary>
    private static readonly HashSet<Type> GrainBlocked = new()
    {
        typeof(AgentSessionResolver),
        typeof(WorkflowSessionHealthService),
        typeof(WorkflowArtifactUploadService),
        typeof(AgentJobArtifactUploadService),
        typeof(RunnerStatusService),
    };

    public static IEnumerable<object[]> MigratedServices()
    {
        // Domain services (Project / Issue / Agent / Epic / Label / Sessions)
        yield return new object[] { typeof(ProjectQuerier), ServiceLifetime.Singleton };
        yield return new object[] { typeof(ProjectRefResolver), ServiceLifetime.Singleton };
        yield return new object[] { typeof(IssueRepositoryResolver), ServiceLifetime.Singleton };
        yield return new object[] { typeof(IssueIdentityResolver), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueMetricsQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueReadModelLoader), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(EpicQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(LabelCatalogService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueWorkflowProfileRegistry), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueTemplateRegistry), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentSessionQuery), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentSessionQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentActivityFeedAssembler), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentUsageReporter), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentSessionResolver), ServiceLifetime.Scoped };

        // Workflow services
        yield return new object[] { typeof(PromptTemplateEngine), ServiceLifetime.Singleton };
        yield return new object[] { typeof(WorkflowSessionHealthService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(WorkflowActivityQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(WorkflowQuerier), ServiceLifetime.Scoped };
        yield return new object[] { typeof(WorkflowProfileManager), ServiceLifetime.Scoped };
        yield return new object[] { typeof(WorkflowRunProfileManager), ServiceLifetime.Scoped };
        yield return new object[] { typeof(WorkflowItemTranslator), ServiceLifetime.Scoped };
        yield return new object[] { typeof(ProjectWorkflowProfileManager), ServiceLifetime.Scoped };
        yield return new object[] { typeof(IssueWorkflowProfileManager), ServiceLifetime.Scoped };

        // Runner / System / Artifact services
        yield return new object[] { typeof(ConnectionSubscriptionRegistry), ServiceLifetime.Singleton };
        yield return new object[] { typeof(ConfigService), ServiceLifetime.Singleton };
        yield return new object[] { typeof(RuntimeBuildInfo), ServiceLifetime.Singleton };
        yield return new object[] { typeof(SystemdInstallDetector), ServiceLifetime.Singleton };
        yield return new object[] { typeof(SystemInfoService), ServiceLifetime.Singleton };
        yield return new object[] { typeof(SystemUpdateService), ServiceLifetime.Singleton };
        yield return new object[] { typeof(RunnerConnectionTracker), ServiceLifetime.Singleton };
        yield return new object[] { typeof(AttachmentService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(WorkflowArtifactUploadService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(AgentJobArtifactUploadService), ServiceLifetime.Scoped };
        yield return new object[] { typeof(RunnerStatusService), ServiceLifetime.Scoped };
    }

    public static IEnumerable<object[]> MigratedServicesResolvableThroughDbFixture()
    {
        foreach (var row in MigratedServices())
        {
            var type = (Type)row[0];
            if (GrainBlocked.Contains(type)) continue;
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

        if (GrainBlocked.Contains(serviceType))
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
