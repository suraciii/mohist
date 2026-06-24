using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Foundation;

[Collection("MohistDb")]
public class ConventionalRegistrationSpecs
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private readonly MohistDbFixture _fixture;

    public ConventionalRegistrationSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void ScopedMarker_RegistersAsSelfWithScopedLifetime_AndIsResolvable()
    {
        var services = new ServiceCollection();
        services.AddMohistConventionalServices(typeof(ConventionalScopedProbe).Assembly);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var fromScope1 = scope.ServiceProvider.GetService<ConventionalScopedProbe>();
        var fromScope2 = scope.ServiceProvider.GetService<ConventionalScopedProbe>();

        Assert.NotNull(fromScope1);
        Assert.NotNull(fromScope2);
        Assert.Same(fromScope1, fromScope2);

        using var otherScope = provider.CreateScope();
        var fromOtherScope = otherScope.ServiceProvider.GetService<ConventionalScopedProbe>();
        Assert.NotSame(fromScope1, fromOtherScope);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void SingletonMarker_RegistersAsSelfWithSingletonLifetime_AndIsResolvable()
    {
        var services = new ServiceCollection();
        services.AddMohistConventionalServices(typeof(ConventionalSingletonProbe).Assembly);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var fromScope = scope.ServiceProvider.GetService<ConventionalSingletonProbe>();
        var fromRoot = provider.GetService<ConventionalSingletonProbe>();

        Assert.NotNull(fromScope);
        Assert.Same(fromScope, fromRoot);

        fromScope!.Counter = 7;
        Assert.Equal(7, provider.GetService<ConventionalSingletonProbe>()!.Counter);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void ScannedTypes_AreResolvableThroughProductionAndFixtureRegistrationEntries()
    {
        var productionServices = new ServiceCollection();
        productionServices.AddMohistConventionalServices(typeof(ConventionalScopedProbe).Assembly);
        using var productionProvider = productionServices.BuildServiceProvider();

        using (var scope = productionProvider.CreateScope())
        {
            var fromProd = scope.ServiceProvider.GetService<ConventionalScopedProbe>();
            Assert.NotNull(fromProd);
        }

        var fixtureServices = new ServiceCollection();
        fixtureServices.AddMohistConventionalServices(typeof(ConventionalScopedProbe).Assembly);
        using var fixtureProvider = fixtureServices.BuildServiceProvider();
        using var fixtureScope = fixtureProvider.CreateScope();
        var fromFixture = fixtureScope.ServiceProvider.GetService<ConventionalScopedProbe>();
        Assert.NotNull(fromFixture);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void TestProbeTypes_AreNotRegisteredThroughProductionEntry()
    {
        var services = new ServiceCollection();
        services.AddMohistServerCore(EmptyConfig());

        Assert.DoesNotContain(services, d => d.ServiceType.Namespace == typeof(ConventionalScopedProbe).Namespace);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void TypesWithoutAnyMarkerInterface_AreNotAutoRegistered()
    {
        var services = new ServiceCollection();
        services.AddMohistConventionalServices(typeof(UnmarkedProbe).Assembly);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<UnmarkedProbe>());
        Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<UnmarkedProbe>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Foundation)]
    [Fact]
    public void ExplicitHandWrittenRegistrationAfterScan_WinsAndDoesNotThrow()
    {
        const int replacementMarker = 4242;

        var services = new ServiceCollection();
        services.AddMohistConventionalServices(typeof(ConventionalOverrideProbe).Assembly);
        services.AddSingleton(new ConventionalOverrideProbe { Marker = replacementMarker });

        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetService<ConventionalOverrideProbe>();
        Assert.NotNull(resolved);
        Assert.Equal(replacementMarker, resolved!.Marker);

        var lastDescriptor = services.Last(d => d.ServiceType == typeof(ConventionalOverrideProbe));
        Assert.Equal(ServiceLifetime.Singleton, lastDescriptor.Lifetime);
        Assert.Equal(replacementMarker, ((ConventionalOverrideProbe)lastDescriptor.ImplementationInstance!).Marker);
    }
}
