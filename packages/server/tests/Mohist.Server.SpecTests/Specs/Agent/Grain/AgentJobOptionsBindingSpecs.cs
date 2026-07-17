using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Orleans;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("RunnerGrain")]
public class AgentJobOptionsBindingSpecs
{
    private readonly WorkflowGrainFixture _fixture;

    public AgentJobOptionsBindingSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AgentJobOptions_ResolveBackoffSchedule_UsesDefaultsFromConfigurationKnob()
    {
        var provider = _fixture.Cluster.GetSiloServiceProvider(null);
        var optionsAccessor = provider.GetRequiredService<IOptions<AgentJobOptions>>();
        var schedule = optionsAccessor.Value.ResolveBackoffSchedule();

        Assert.Equal(TimeSpan.FromMilliseconds(50), schedule.Initial);
        Assert.Equal(TimeSpan.FromMilliseconds(200), schedule.Cap);
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.TotalBound);
        Assert.Equal(TimeSpan.FromSeconds(10), optionsAccessor.Value.JobTimeout);

        var next = schedule.NextDelay(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(50), next);

        var capped = schedule.NextDelay(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), capped);
    }
}
