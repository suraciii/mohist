using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.SpecTests.Specs.Events;

[Subscription(Type = EventCatalog.ReverseDns.IssueCompleted)]
public sealed class DispatcherClosedGenericHandler : ICloudEventHandler<IssueCompleted>
{
    private readonly DispatcherFixture _fixture;

    public DispatcherClosedGenericHandler(DispatcherFixture fixture) => _fixture = fixture;

    public bool Filter(CloudEvent<IssueCompleted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCompleted> evt, CancellationToken ct)
    {
        lock (_fixture.ClosedGenericInvocations)
            _fixture.ClosedGenericInvocations.Add(evt.Id);
        return Task.CompletedTask;
    }
}

[Subscription(Type = "*")]
public sealed class DispatcherCatchAllHandler : ICloudEventHandler
{
    private readonly DispatcherFixture _fixture;

    public DispatcherCatchAllHandler(DispatcherFixture fixture) => _fixture = fixture;

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        lock (_fixture.CatchAllInvocations)
            _fixture.CatchAllInvocations.Add(evt.Id);
        _fixture.RecordCatchAllInvocation(evt.Id);
        EventDispatcherImmediateTriggerTestSupport.RecordHandlerInvocation(
            _fixture,
            DispatcherHandler.CatchAll,
            evt);
        return Task.CompletedTask;
    }
}

[Subscription(Type = EventCatalog.ReverseDns.WorkflowRunCompleted)]
public sealed class DispatcherSpecificHandler : ICloudEventHandler
{
    private readonly DispatcherFixture _fixture;

    public DispatcherSpecificHandler(DispatcherFixture fixture) => _fixture = fixture;

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        _fixture.RecordSpecificInvocation(evt.Id);
        EventDispatcherImmediateTriggerTestSupport.RecordHandlerInvocation(
            _fixture,
            DispatcherHandler.Specific,
            evt);
        return Task.CompletedTask;
    }
}

public sealed class TestNoopTranscriptEventPublisher : ITranscriptEventPublisher
{
    public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}

[Subscription(Type = "test.poison")]
public sealed class DispatcherPoisonHandler : ICloudEventHandler
{
    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) =>
        throw new InvalidOperationException("poison test handler");
}
