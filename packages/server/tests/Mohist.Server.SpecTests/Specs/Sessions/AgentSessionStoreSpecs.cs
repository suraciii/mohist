using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Support.TestData;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public class AgentSessionStoreSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly AgentSessionStore _store;
    private readonly AgentSessionTranscriptStore _transcriptStore;

    public AgentSessionStoreSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _store = new AgentSessionStore(new TestDbContextFactory(_database.Options), new NoopEventStore(), new NullDispatchGrainFactory(), NullLogger<AgentSessionStore>.Instance);
        _transcriptStore = new AgentSessionTranscriptStore(new TestDbContextFactory(_database.Options));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveAndLoad_PreservesCurrentRuntimeBindingAndLineage()
    {
        var createdAt = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var session = AgentSession.Create(
            $"session-{Guid.NewGuid():N}",
            "runner-1",
            "/work",
            metadata: WorkflowMetadata(),
            now: createdAt,
            runtime: "opencode");
        session.AttachPhysicalSession(
            "runtime-session-1",
            model: null,
            workDir: "/work",
            changeDir: null,
            processPid: null,
            now: createdAt.AddMinutes(1));

        await _store.SaveAsync(session.Id, session);
        var rehydrated = await _store.LoadAsync(session.Id);

        Assert.NotNull(rehydrated);
        Assert.Equal("opencode", rehydrated!.Runtime.Runtime);
        Assert.Equal("runtime-session-1", rehydrated.Status.AgentRuntimeSessionId);
        Assert.Equal("runner-1", rehydrated.Runtime.RunnerId);
        Assert.Equal("/work", rehydrated.Runtime.WorkDir);
        Assert.Equal("opencode", Assert.Single(rehydrated.Status.RuntimeSessionLineage!).Runtime);
    }

    [Fact]
    public async Task Load_LegacyStateWithoutRuntime_RemainsQueryableWithoutRewrite()
    {
        var createdAt = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var session = AgentSession.Create(
            $"legacy-session-{Guid.NewGuid():N}",
            "runner-1",
            "/work",
            metadata: WorkflowMetadata(),
            now: createdAt,
            runtime: "opencode");
        session.AttachPhysicalSession(
            "legacy-runtime-session",
            model: null,
            workDir: "/work",
            changeDir: null,
            processPid: null,
            now: createdAt.AddMinutes(1));
        await _store.SaveAsync(session.Id, session);

        string legacyState;
        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.AgentSessions.SingleAsync(candidate => candidate.Id == session.Id);
            var state = JsonNode.Parse(row.State)!.AsObject();
            state["runtime"]!.AsObject().Remove("runtime");
            state["status"]!["runtimeSessionLineage"]![0]!.AsObject().Remove("runtime");
            var labels = state["metadata"]!["labels"]!.AsObject();
            labels.Remove("mohist.io/project-id");
            labels.Remove("mohist.io/source-kind");
            labels.Remove("mohist.io/source-id");
            labels.Remove("mohist.io/session-name");
            legacyState = state.ToJsonString();
            row.State = legacyState;
            await db.SaveChangesAsync();
        }

        var rehydrated = await _store.LoadAsync(session.Id);

        Assert.NotNull(rehydrated);
        Assert.Equal("legacy-runtime-session", rehydrated!.Status.AgentRuntimeSessionId);
        Assert.Null(rehydrated.Runtime.Runtime);
        Assert.Null(Assert.Single(rehydrated.Status.RuntimeSessionLineage!).Runtime);
        await using var verificationDb = new MohistDbContext(_database.Options);
        var persistedState = await verificationDb.AgentSessions
            .Where(candidate => candidate.Id == session.Id)
            .Select(candidate => candidate.State)
            .SingleAsync();
        Assert.Equal(legacyState, persistedState);
    }

    [Fact]
    public async Task SavePartsAsync_RetrySameCorrelationKey_UpdatesExistingPart()
    {
        var sessionId = $"transcript-{Guid.NewGuid():N}";
        var now = TestTime.UtcDateTime;
        var turn = new AgentSessionTranscriptTurnUpsert(sessionId, 1, "prompt", "task", now, now);
        var parts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-1", null, "hello", "{}", now, now, 1)
        };

        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(true, turn, parts));

        var retryParts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-1", null, " world", "{}", now, now, 1)
        };
        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(false, turn, retryParts));

        await using var db = new MohistDbContext(_database.Options);
        var partRows = await db.AgentSessionTranscriptParts.ToListAsync();
        var part = Assert.Single(partRows);
        Assert.Equal("hello world", part.Text);
    }

    private static AgentSessionMetadata WorkflowMetadata() =>
        new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build");

    [Fact]
    public async Task SavePartsAsync_NewCorrelationKey_InsertsAdditionalPart()
    {
        var sessionId = $"transcript-{Guid.NewGuid():N}";
        var now = TestTime.UtcDateTime;
        var turn = new AgentSessionTranscriptTurnUpsert(sessionId, 1, "prompt", "task", now, now);
        var firstParts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-1", null, "hello", "{}", now, now, 1)
        };
        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(true, turn, firstParts));

        var secondParts = new[]
        {
            new AgentSessionTranscriptPartDelta("text", "msg-2", null, "world", "{}", now, now, 1)
        };
        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(false, turn, secondParts));

        await using var db = new MohistDbContext(_database.Options);
        var partRows = await db.AgentSessionTranscriptParts.OrderBy(p => p.Sequence).ToListAsync();
        Assert.Equal(2, partRows.Count);
        Assert.Equal("msg-1", partRows[0].CorrelationKey);
        Assert.Equal("msg-2", partRows[1].CorrelationKey);
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
