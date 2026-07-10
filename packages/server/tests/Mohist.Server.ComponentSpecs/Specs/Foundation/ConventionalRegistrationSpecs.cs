using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.ComponentSpecs.Support;
using Xunit;

namespace Mohist.Server.ComponentSpecs.Specs.Foundation;

[Collection("MohistDb")]
public class ConventionalRegistrationSpecs
{
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
}
