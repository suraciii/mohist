using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Issue.Profile;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Events;

/// <summary>
/// In-proc spec harness for the stream-lease dispatch engine. Builds the
/// production dispatcher graph (real <see cref="DispatchStreamLeaseStore"/>
/// on SQLite, production subscription discovery, <see cref="EventDispatcherService"/>)
/// around capturing event/dead-letter fakes. No Orleans silo: the engine
/// is plain .NET and the specs drive it with explicit drains.
/// </summary>
public sealed class DispatcherFixture : IAsyncLifetime
{
    public FakeTimeProvider TimeProvider { get; } = new(TestTime.UtcNow);
    public CapturingEventStore EventStore { get; } = new();
    public CapturingEventPublisher EventPublisher { get; } = new();
    public CapturingDeadLetterStore DeadLetterStore { get; }
    public EventDispatchSignal DispatchSignal { get; } = new();
    internal DispatcherDeliverySignals DeliverySignals { get; } = new();

    public IServiceProvider Services { get; private set; } = null!;
    public IEventDispatcher EventDispatcher { get; private set; } = null!;

    /// <summary>
    /// Call lists shared by the test handlers
    /// (<see cref="DispatcherClosedGenericHandler"/>,
    /// <see cref="DispatcherCatchAllHandler"/>,
    /// <see cref="DispatcherSpecificHandler"/>) via the service provider.
    /// The handlers resolve the fixture instance from DI so they can
    /// record invocations here.
    /// </summary>
    public List<string> ClosedGenericInvocations { get; } = [];
    public List<string> CatchAllInvocations { get; } = [];
    public List<string> SpecificInvocations { get; } = [];
    private readonly Dictionary<string, TaskCompletionSource> _specificDeliverySignals = new(StringComparer.Ordinal);
    private readonly object _specificDeliverySignalsGate = new();
    private readonly Dictionary<string, TaskCompletionSource> _catchAllDeliverySignals = new(StringComparer.Ordinal);
    private readonly object _catchAllDeliverySignalsGate = new();

    private SqliteConnection _keeper = null!;

    public DispatcherFixture()
    {
        DeadLetterStore = new CapturingDeadLetterStore(EventStore);
        EventStore.SettlementObserver = row =>
            EventDispatcherImmediateTriggerTestSupport.RecordEventSettlement(this, row);
    }

    public async ValueTask InitializeAsync()
    {
        var dbName = $"mohist-dispatcher-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        // Provision the production schema so the real lease store has the
        // DispatchStreamLeases table, and the producer-side stores have the
        // WorkflowRunEvents / IssueEvents / AgentSessionEvents tables.
        MigratedSqliteTemplate.CopyTo(_keeper);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider);
        services.AddDbContextFactory<MohistDbContext>(o => o.UseSqlite(connectionString));

        services.AddSingleton<IEventStore>(EventStore);
        services.AddSingleton<IDeadLetterStore>(DeadLetterStore);
        services.AddSingleton(DispatchSignal);
        services.AddSingleton<EventDispatchSignal>(DispatchSignal);

        services.AddCloudEventBus();
        services.AddSingleton(this);
        CloudEventBusServiceCollectionExtensions.AddCloudEventHandlers(
            services,
            [
                typeof(DispatcherClosedGenericHandler),
                typeof(DispatcherCatchAllHandler),
                typeof(DispatcherSpecificHandler),
                typeof(DispatcherPoisonHandler),
            ]);

        services.AddSingleton<IDispatchStreamLeaseStore, DispatchStreamLeaseStore>();
        services.AddSingleton<IEventPushQueue>(NullEventPushQueue.Instance);
        services.Configure<EventDispatcherOptions>(options =>
        {
            options.MaxAttempts = 3;
            options.BaseBackoff = TimeSpan.Zero;
            options.MaxBackoff = TimeSpan.Zero;
        });
        services.AddSingleton<EventDispatcherService>();
        services.AddSingleton<IEventDispatcher>(sp => sp.GetRequiredService<EventDispatcherService>());

        // Producer-side stores for the wake-signal specs. Their grain
        // counterparts compile against the same wake wiring.
        services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        services.AddScoped<IDispatchSnapshotStore, DispatchSnapshotStore>();
        services.AddScoped<IIssueStore, IssueStore>();
        services.AddScoped<IAgentSessionStore, AgentSessionStore>();
        services.AddScoped<IAgentJobStore, AgentJobStore>();

        Services = services.BuildServiceProvider();
        EventDispatcher = Services.GetRequiredService<IEventDispatcher>();
        EventPublisher.RegisterSink(EventStore);
    }

    public void ResetInvocationRecords()
    {
        EventStore.Reset();
        DeadLetterStore.Reset();
        lock (ClosedGenericInvocations)
            ClosedGenericInvocations.Clear();
        lock (CatchAllInvocations)
            CatchAllInvocations.Clear();
        lock (SpecificInvocations)
            SpecificInvocations.Clear();
        lock (_specificDeliverySignalsGate)
            _specificDeliverySignals.Clear();
        lock (_catchAllDeliverySignalsGate)
            _catchAllDeliverySignals.Clear();
        EventDispatcherImmediateTriggerTestSupport.ResetHandlerDeliveries(this);
    }

    public Task WaitForSpecificInvocationAsync(string eventId)
    {
        lock (_specificDeliverySignalsGate)
        {
            return GetSpecificDeliverySignal(eventId).Task;
        }
    }

    public Task WaitForCatchAllInvocationAsync(string eventId)
    {
        lock (_catchAllDeliverySignalsGate)
        {
            return GetCatchAllDeliverySignal(eventId).Task;
        }
    }

    public void RecordSpecificInvocation(string eventId)
    {
        lock (SpecificInvocations)
            SpecificInvocations.Add(eventId);
        lock (_specificDeliverySignalsGate)
            GetSpecificDeliverySignal(eventId).TrySetResult();
    }

    public void RecordCatchAllInvocation(string eventId)
    {
        lock (_catchAllDeliverySignalsGate)
            GetCatchAllDeliverySignal(eventId).TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Services is IDisposable d)
            d.Dispose();
        await _keeper.DisposeAsync();
    }

    private TaskCompletionSource GetSpecificDeliverySignal(string eventId) =>
        _specificDeliverySignals.TryGetValue(eventId, out var signal)
            ? signal
            : _specificDeliverySignals[eventId] = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource GetCatchAllDeliverySignal(string eventId) =>
        _catchAllDeliverySignals.TryGetValue(eventId, out var signal)
            ? signal
            : _catchAllDeliverySignals[eventId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
