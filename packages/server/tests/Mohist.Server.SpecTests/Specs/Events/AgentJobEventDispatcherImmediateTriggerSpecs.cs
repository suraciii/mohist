using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// AgentJob immediate-trigger and persisted reminder-recovery behavior.
/// </summary>
[Collection("Dispatcher")]
public sealed class AgentJobEventDispatcherImmediateTriggerSpecs
{
    private readonly DispatcherFixture _fixture;

    public AgentJobEventDispatcherImmediateTriggerSpecs(DispatcherFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetInvocationRecords();
    }

    [Fact]
    public async Task FailureEventAppend_PokesDispatcherBeforeReminderCadence()
    {
        var jobKey = $"agent-job-poke-{Guid.NewGuid():N}";
        var delivery = EventDispatcherImmediateTriggerTestSupport.ExpectPokeDelivery(
            _fixture,
            row => row.Source == $"/mohist/agent-job/{jobKey}"
                && row.Type == EventCatalog.ReverseDns.AgentJobFailed,
            DispatcherHandler.CatchAll);
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);

        await job.FailAsync("runner-lost", "agent-test");

        await EventDispatcherImmediateTriggerTestSupport.AwaitPokeDeliveryAsync(_fixture, delivery);
        Assert.True(_fixture.BackgroundTasks.LaunchCount > 0);
    }

    [Fact]
    public async Task AppendFailure_DoesNotPokeAndRetainsRecoveryObligation()
    {
        var jobKey = $"agent-job-poke-recovery-{Guid.NewGuid():N}";
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
        _fixture.EventStore.ThrowOnAppend = envelope =>
            envelope.Type == EventCatalog.ReverseDns.AgentJobFailed;

        await job.FailAsync("runner-lost", "agent-test");

        Assert.Equal(0, _fixture.BackgroundTasks.LaunchCount);
        _fixture.EventStore.ThrowOnAppend = null;
        var delivery = EventDispatcherImmediateTriggerTestSupport.ExpectPokeDelivery(
            _fixture,
            row => row.Source == $"/mohist/agent-job/{jobKey}"
                && row.Type == EventCatalog.ReverseDns.AgentJobFailed,
            DispatcherHandler.CatchAll);

        var reactivatedJob = await _fixture.DeactivateAndReactivateAgentJobAsync(jobKey);
        Assert.Equal(AgentJobStatus.Failed, await reactivatedJob.GetStatusAsync());

        _fixture.BackgroundTasks.RequireExpectedLaunch(
            delivery.PokeEnqueued,
            "AgentJob activation recovery did not enqueue a dispatcher poke");
        var pending = await _fixture.EventStore.ListUndeliveredAsync();
        var matching = pending
            .Where(row => row.Source == $"/mohist/agent-job/{jobKey}"
                && row.Type == EventCatalog.ReverseDns.AgentJobFailed)
            .ToArray();
        Assert.Single(matching);
        var agentJobEvent = matching[0];
        Assert.Equal(EventOrigin.AgentJob, agentJobEvent.Origin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fixture.EventStore.MarkDispatchedAsync(
                EventOrigin.AgentJob,
                agentJobEvent.Source,
                agentJobEvent.Id + 1,
                DateTimeOffset.UnixEpoch));
        var retained = Assert.Single(await _fixture.EventStore.ListUndeliveredAsync());
        Assert.Equal(agentJobEvent.Id, retained.Id);
        Assert.Equal(agentJobEvent.Source, retained.Source);
        Assert.Equal(agentJobEvent.EventId, retained.EventId);

        await EventDispatcherImmediateTriggerTestSupport.AwaitPokeDeliveryAsync(_fixture, delivery);
        Assert.True(_fixture.BackgroundTasks.LaunchCount > 0);

        _fixture.EventStore.AddUndeliveredShadow(agentJobEvent with
        {
            Origin = EventOrigin.WorkflowRun,
            EventId = $"{agentJobEvent.EventId}-workflow-shadow",
            Type = "com.mohist.workflow.shadow",
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fixture.EventStore.MarkDispatchedAsync(
                EventOrigin.AgentJob,
                agentJobEvent.Source,
                agentJobEvent.Id,
                DateTimeOffset.UnixEpoch));
        var remaining = await _fixture.EventStore.ListUndeliveredAsync();
        var shadow = Assert.Single(remaining);
        Assert.Equal(EventOrigin.WorkflowRun, shadow.Origin);
        Assert.Equal(agentJobEvent.Source, shadow.Source);
        Assert.Equal(agentJobEvent.Id, shadow.Id);
    }
}
