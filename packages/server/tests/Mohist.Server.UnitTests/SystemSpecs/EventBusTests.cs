using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class EventBusTests
{
    private static readonly FakeTimeProvider TestTime = new(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task PublishAsync_NoSubscriber_DoesNotThrow()
    {
        var bus = new InMemoryEventBus(new NoopEventStore(), TestTime, NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(
            data: new TestPayload("orphan"),
            type: "test.orphan",
            source: "test://orphan");
    }

    [Fact]
    public async Task PublishAsync_WithSubscriber_DoesNotInvokeHandler()
    {
        var store = new RecordingEventStore();
        var received = new Queue<CloudEvent>();
        var subs = new List<Subscription>
        {
            new("test.greeting", new RecordingHandler(
                filter: _ => true,
                onEvent: e => received.Enqueue(e)),
                DispatchDynamic),
        };
        var bus = new InMemoryEventBus(subs, store, TestTime, NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(
            data: new TestPayload("hello"),
            type: "test.greeting",
            source: "test://greeting");

        Assert.Empty(received);
        var recorded = Assert.Single(store.Appended);
        Assert.Equal("test.greeting", recorded.Envelope.Type);
        Assert.Equal("test://greeting/", recorded.Envelope.Source.ToString());
    }

    [Fact]
    public async Task PublishAsync_TypedOverload_PreservesSubjectDataAndExtensions()
    {
        var store = new RecordingEventStore();
        var bus = new InMemoryEventBus(store, TestTime, NullLogger<InMemoryEventBus>.Instance);
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal) { ["traceId"] = "tr_typed" };

        await bus.PublishAsync(
            data: new TestPayload("hello"),
            type: "test.greeting",
            source: "test://greeting",
            subject: "subj-9",
            extensions: extensions);

        var recorded = Assert.Single(store.Appended);
        Assert.Equal("test.greeting", recorded.Envelope.Type);
        Assert.Equal("test://greeting/", recorded.Envelope.Source.ToString());
        Assert.Equal("subj-9", recorded.Envelope.Subject);
        Assert.Equal("tr_typed", recorded.Envelope.Extensions["traceId"]);
        Assert.Contains("\"message\":\"hello\"", recorded.Envelope.Data!.Value.GetRawText());
    }

    [Fact]
    public async Task PublishAsync_FilteredOut_HandlerNotInvoked()
    {
        var store = new RecordingEventStore();
        var received = new Queue<CloudEvent>();
        var subs = new List<Subscription>
        {
            new("test.greeting", new RecordingHandler(
                filter: e => false,
                onEvent: e => received.Enqueue(e)),
                DispatchDynamic),
        };
        var bus = new InMemoryEventBus(subs, store, TestTime, NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(
            data: new TestPayload("hello"),
            type: "test.greeting",
            source: "test://greeting");

        Assert.Empty(received);
        Assert.Single(store.Appended);
    }

    [Fact]
    public async Task PublishAsync_CloudEventOverload_AppendsSingleRowPreservingEnvelope()
    {
        var store = new RecordingEventStore();
        var bus = new InMemoryEventBus(store, TestTime, NullLogger<InMemoryEventBus>.Instance);

        var data = JsonDocument.Parse("{\"k\":\"v\"}").RootElement;
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal) { ["traceId"] = "tr_1" };
        var envelope = new CloudEvent(
            id: "evt-raw-1",
            source: new Uri("test://raw"),
            type: "test.raw",
            time: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            data: data,
            dataContentType: "application/json",
            subject: "subj-1",
            extensions: extensions);

        await bus.PublishAsync(envelope);

        var recorded = Assert.Single(store.Appended);
        Assert.Same(envelope, recorded.Envelope);
        Assert.Equal("test.raw", recorded.Envelope.Type);
        Assert.Equal("test://raw/", recorded.Envelope.Source.ToString());
        Assert.Equal("subj-1", recorded.Envelope.Subject);
        Assert.Equal("tr_1", recorded.Envelope.Extensions["traceId"]);
    }

    [Fact]
    public async Task PublishAsync_MatchingThrowingHandler_DoesNotDispatchAndAppendsRow()
    {
        // Publish is write-only: even a matching handler that would throw if
        // dispatched must not affect the publish path. The row is appended and
        // the handler is never invoked.
        var store = new RecordingEventStore();
        var received = new Queue<CloudEvent>();
        var subs = new List<Subscription>
        {
            new("test.greeting", new RecordingHandler(
                filter: _ => true,
                onEvent: _ => throw new InvalidOperationException("handler exploded")),
                DispatchDynamic),
        };
        var bus = new InMemoryEventBus(subs, store, TestTime, NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(
            data: new TestPayload("hello"),
            type: "test.greeting",
            source: "test://greeting");

        Assert.Empty(received);
        Assert.Single(store.Appended);
    }

    [Fact]
    public void HandlerRegistration_InvalidSubscriptionPatternFailsImmediately()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() =>
            services.AddCloudEventHandlers([typeof(InvalidPatternHandler)]));

        Assert.Contains("wildcards are only allowed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Subscription_DefaultsIdentityToHandlerRuntimeFullName()
    {
        var handler = new RecordingHandler(_ => true, _ => { });

        var subscription = new Subscription("test.greeting", handler, DispatchDynamic);

        Assert.Equal(typeof(RecordingHandler).FullName, subscription.Identity);
    }

    [Fact]
    public void Subscription_AcceptsExplicitDurableIdentity()
    {
        var handler = new RecordingHandler(_ => true, _ => { });
        const string legacy = "Mohist.Server.Events.Subscriptions.RecordingHandler";

        var subscription = new Subscription("test.greeting", handler, DispatchDynamic, legacy);

        Assert.Equal(legacy, subscription.Identity);
    }

    [Fact]
    public void Subscription_ExplicitIdentityOverridesRuntimeFullName()
    {
        var handler = new RecordingHandler(_ => true, _ => { });

        var subscription = new Subscription(
            "test.greeting",
            handler,
            DispatchDynamic,
            "custom.durable.identity");

        Assert.NotEqual(typeof(RecordingHandler).FullName, subscription.Identity);
        Assert.Equal("custom.durable.identity", subscription.Identity);
    }

    [Fact]
    public void AddCloudEventHandlers_UsesAttributeIdentity_WhenDeclared()
    {
        var services = new ServiceCollection();

        services.AddCloudEventHandlers([typeof(IdentityDeclaredHandler)]);

        var subscriptions = services
            .BuildServiceProvider()
            .GetRequiredService<IEnumerable<Subscription>>();

        var sub = Assert.Single(subscriptions);
        Assert.Equal("Mohist.Server.Events.Subscriptions.PreservedIdentity", sub.Identity);
    }

    [Fact]
    public void AddCloudEventHandlers_FallsBackToRuntimeFullName_WhenAttributeIdentityOmitted()
    {
        var services = new ServiceCollection();

        services.AddCloudEventHandlers([typeof(IdentityOmittedHandler)]);

        var subscriptions = services
            .BuildServiceProvider()
            .GetRequiredService<IEnumerable<Subscription>>();

        var sub = Assert.Single(subscriptions);
        Assert.Equal(typeof(IdentityOmittedHandler).FullName, sub.Identity);
    }

    private sealed record TestPayload(string Message);

    private static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }

    [Subscription(Type = "test.greeting")]
    private sealed class RecordingHandler : ICloudEventHandler
    {
        private readonly Func<CloudEvent, bool> _filter;
        private readonly Action<CloudEvent> _onEvent;

        public RecordingHandler(Func<CloudEvent, bool> filter, Action<CloudEvent> onEvent)
        {
            _filter = filter;
            _onEvent = onEvent;
        }

        public bool Filter(CloudEvent evt) => _filter(evt);

        public Task HandleAsync(CloudEvent evt, CancellationToken ct)
        {
            _onEvent(evt);
            return Task.CompletedTask;
        }

        public Task OnEvent(CloudEvent evt)
        {
            _onEvent(evt);
            return Task.CompletedTask;
        }
    }

    [Subscription(Type = "test.*.invalid")]
    private sealed class InvalidPatternHandler : ICloudEventHandler
    {
        public bool Filter(CloudEvent evt) => true;

        public Task HandleAsync(CloudEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    [Subscription(Type = "test.identity.declared", Identity = "Mohist.Server.Events.Subscriptions.PreservedIdentity")]
    private sealed class IdentityDeclaredHandler : ICloudEventHandler
    {
        public bool Filter(CloudEvent evt) => true;

        public Task HandleAsync(CloudEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    [Subscription(Type = "test.identity.omitted")]
    private sealed class IdentityOmittedHandler : ICloudEventHandler
    {
        public bool Filter(CloudEvent evt) => true;

        public Task HandleAsync(CloudEvent evt, CancellationToken ct) => Task.CompletedTask;
    }
}
