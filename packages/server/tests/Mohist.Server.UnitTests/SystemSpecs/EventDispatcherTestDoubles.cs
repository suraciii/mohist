using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;
namespace Mohist.Server.UnitTests.SystemSpecs;

internal sealed class Recorder : ICloudEventHandler
{
    private readonly Action<CloudEvent> _onEvent;

    public Recorder(Action<CloudEvent> onEvent) => _onEvent = onEvent;

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        _onEvent(evt);
        return Task.CompletedTask;
    }
}

internal sealed class FlakyRecorder : ICloudEventHandler
{
    private readonly Action _onEvent;

    public FlakyRecorder(Action onEvent) => _onEvent = onEvent;

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        _onEvent();
        return Task.CompletedTask;
    }
}

internal sealed class CapturingTypedHandler : ICloudEventHandler<IssueCompleted>
{
    private readonly List<CloudEvent<IssueCompleted>> _sink;

    public CapturingTypedHandler(List<CloudEvent<IssueCompleted>> sink) => _sink = sink;

    public bool Filter(CloudEvent<IssueCompleted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCompleted> evt, CancellationToken ct)
    {
        _sink.Add(evt);
        return Task.CompletedTask;
    }
}

internal sealed class IdempotentRecorder : ICloudEventHandler
{
    private readonly Action<CloudEvent> _onEvent;

    public IdempotentRecorder(Action<CloudEvent> onEvent) => _onEvent = onEvent;

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        _onEvent(evt);
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() =>
        throw new InvalidOperationException("launch unavailable");
}
