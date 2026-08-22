using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public sealed class DispatcherStartupSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public DispatcherStartupSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void HostStartup_RegistersDispatcherEngineAndWorkers()
    {
        // The stream-lease engine replaces the reminder grain: the host
        // must expose IEventDispatcher and run the dispatch workers as a
        // hosted service.
        Assert.NotNull(_fixture.Services.GetRequiredService<IEventDispatcher>());
        var hosted = _fixture.Services.GetServices<IHostedService>();
        Assert.Contains(hosted, service => service is EventDispatchWorker);
    }
}
