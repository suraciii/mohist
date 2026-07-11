using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.SpecTests.Support;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task HostStartup_ActivatesDispatcherAndRegistersReminder()
    {
        var dispatcherId = _fixture.Grains
            .GetGrain<IDispatcherGrain>(DispatcherGrain.FixedKey)
            .GetGrainId();
        var reminderTable = _fixture.Services.GetRequiredService<IReminderTable>();

        var row = await reminderTable.ReadRow(dispatcherId, DispatcherGrain.ReminderName);

        Assert.NotNull(row);
        Assert.Equal(DispatcherGrain.ReminderName, row.ReminderName);
    }
}
