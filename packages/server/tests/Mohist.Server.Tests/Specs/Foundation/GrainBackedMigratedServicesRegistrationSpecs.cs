using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mohist.Server.Infrastructure.Hosting;
using Orleans;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Xunit;

namespace Mohist.Server.Tests.Specs.Foundation;

[Collection("WorkflowGrain")]
public sealed class GrainBackedMigratedServicesRegistrationSpecs
{
    private readonly WorkflowGrainFixture _fixture;

    public GrainBackedMigratedServicesRegistrationSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    private static IConfiguration EmptyConfig(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:SqliteConnectionString"] = connectionString,
                ["Mohist:ServerUrl"] = "http://127.0.0.1:3456",
            })
            .Build();

    public static IEnumerable<object[]> GrainBackedMigratedServices()
    {
        yield return new object[] { typeof(AgentSessionResolver) };
        yield return new object[] { typeof(WorkflowSessionHealthService) };
        yield return new object[] { typeof(WorkflowArtifactUploadService) };
        yield return new object[] { typeof(AgentJobArtifactUploadService) };
        yield return new object[] { typeof(RunnerStatusService) };
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Theory]
    [MemberData(nameof(GrainBackedMigratedServices))]
    public void GrainBackedMigratedService_ResolvesThroughProductionLikeOrleansFixture(Type serviceType)
    {
        var config = EmptyConfig(_fixture.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.ConfigureMohistServices(config);
        services.RemoveAll<IGrainFactory>();
        services.AddSingleton<IGrainFactory>(_fixture.Grains);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(resolved);
        Assert.Equal(serviceType, resolved!.GetType());

        using var otherScope = provider.CreateScope();
        var fromOtherScope = otherScope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(fromOtherScope);
        Assert.NotSame(resolved, fromOtherScope);
    }
}
