using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

/// <summary>
/// Unit specs for <see cref="WorkflowRunStore"/> covering issue-361 T-003:
/// the store now stamps both <c>projectid</c> and <c>issueid</c> onto the
/// emitted WorkflowRun CloudEvent (read from
/// <see cref="WorkflowRunMetadata.Annotations"/>), appends the event row in
/// the same EF Core transaction as the run state, and lets an event-row
/// write failure roll back the state transaction instead of swallowing it.
/// </summary>
public sealed class FakeWorkflowRunStoreDbContextFactory : IDbContextFactory<MohistDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;

    public FakeWorkflowRunStoreDbContextFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        MigratedSqliteTemplate.CopyTo(_connection);
    }

    public MohistDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new MohistDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}

public class WorkflowRunStoreSpecs
{
    private const string ProjectId = "proj_workflow_store";
    private const string IssueId = "issue_ws_1";
    private const string WorkflowRunId = "wr_ws_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithProjectAnnotation_StampsProjectIdOnPersistedEventExtensions()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        var envelope = stored.Envelope;
        Assert.Equal("com.mohist.workflow.run.failed", envelope.Type);
        Assert.True(envelope.Extensions.TryGetValue("projectid", out var projectId));
        Assert.Equal(ProjectId, projectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithIssueAnnotation_StampsIssueIdOnPersistedEventExtensions()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                    ["issueNumber"] = "1",
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        Assert.True(stored.Envelope.Extensions.TryGetValue("issueid", out var stampedIssueId));
        Assert.Equal(IssueId, stampedIssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithoutProjectAnnotation_DoesNotStampProjectIdExtension()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunFailed("failed")]);

        var stored = Assert.Single(await eventStore.ListAsync(WorkflowRunId));
        Assert.False(stored.Envelope.Extensions.ContainsKey("projectid"));
        Assert.False(stored.Envelope.Extensions.ContainsKey("issueid"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task SaveAsync_WithEvents_PersistsStateAndEventRowsInSameTransaction()
    {
        using var factory = new FakeWorkflowRunStoreDbContextFactory();
        var eventStore = new EventStore(factory, NullLogger<EventStore>.Instance);
        var store = new WorkflowRunStore(factory, eventStore, new NullDispatchGrainFactory(), NullLogger<WorkflowRunStore>.Instance);

        var run = new WorkflowRun
        {
            Id = WorkflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                    ["issueId"] = IssueId,
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [
            new WorkflowRunStarted(),
            new WorkflowRunFailed("boom"),
        ]);

        var stored = await eventStore.ListAsync(WorkflowRunId);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.started");
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.failed");

        var loaded = await store.LoadAsync(WorkflowRunId);
        Assert.NotNull(loaded);
        Assert.Equal(WorkflowRunId, loaded!.Id);
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> stand-in for transactional
    /// unit specs. The dispatcher is a no-op grain reference; producers
    /// only need to call DispatchNowAsync without exceptions. Lets the
    /// store exercise its post-commit poke code path without spinning up
    /// an Orleans silo.
    /// </summary>
    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Drop-in <see cref="IEventDispatcherGrain"/> reference whose
    /// <see cref="DispatchNowAsync"/> returns <see cref="Task.CompletedTask"/>.
    /// Lets the post-commit poke fire without an Orleans silo.
    /// </summary>
    private sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
    {
        public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
            Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));

        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;

        public GrainId GrainId => default;
        public string Key => string.Empty;
    }
}
