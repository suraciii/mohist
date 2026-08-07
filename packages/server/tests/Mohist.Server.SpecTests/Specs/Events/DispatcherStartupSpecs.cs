using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Collection("IntegrationMisc")]
public sealed class DispatcherStartupSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public DispatcherStartupSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HostStartup_ActivatesDispatcherAndRegistersReminder()
    {
        var dispatcherId = _fixture.Grains
            .GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global)
            .GetGrainId();
        var reminderTable = _fixture.Services.GetRequiredService<IReminderTable>();

        var row = await reminderTable.ReadRow(dispatcherId, EventDispatcherGrain.ReminderName);

        Assert.NotNull(row);
        Assert.Equal(EventDispatcherGrain.ReminderName, row.ReminderName);
    }
}
