using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.TestSupport.TestData;
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
        _store = new AgentSessionStore(new TestDbContextFactory(_database.Options), new NoopEventStore(), new NullDispatchGrainFactory(), NullLogger<AgentSessionStore>.Instance, new Mohist.Server.Infrastructure.BackgroundTaskLauncher());
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

    [Fact]
    public async Task SavePartsAsync_ToolUpdatePreservesEarlierRawPayloadFields()
    {
        var sessionId = $"transcript-{Guid.NewGuid():N}";
        var now = TestTime.UtcDateTime;
        var turn = new AgentSessionTranscriptTurnUpsert(sessionId, 1, "prompt", "task", now, now);

        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(
            true,
            turn,
            [new AgentSessionTranscriptPartDelta(
                "tool",
                "tool-1",
                "tool-1",
                null,
                "{\"toolCallId\":\"tool-1\",\"status\":\"in_progress\",\"rawInput\":{\"filePath\":\"README.md\"}}",
                now,
                now,
                1)]));

        await _transcriptStore.SaveAsync(new AgentSessionTranscriptFlush(
            false,
            turn,
            [new AgentSessionTranscriptPartDelta(
                "tool",
                "tool-1",
                "tool-1",
                null,
                "{\"toolCallId\":\"tool-1\",\"status\":\"completed\",\"rawOutput\":{\"content\":\"# Project\"}}",
                now,
                now,
                1)]));

        await using var db = new MohistDbContext(_database.Options);
        var payload = await db.AgentSessionTranscriptParts
            .Where(part => part.CorrelationKey == "tool-1")
            .Select(part => part.PayloadJson)
            .SingleAsync();
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("completed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("README.md", document.RootElement.GetProperty("rawInput").GetProperty("filePath").GetString());
        Assert.Equal("# Project", document.RootElement.GetProperty("rawOutput").GetProperty("content").GetString());
    }

    [Fact]
    public async Task ListByRunnerForReconcile_ReturnsOnlyDurablyBoundNonIdleSessions()
    {
        var matching = CreateBoundSession("session-matching", "runner-1", AgentSessionActivity.Unknown);
        var active = CreateBoundSession("session-active", "runner-1", AgentSessionActivity.Active);
        var idle = CreateBoundSession("session-idle", "runner-1", AgentSessionActivity.Idle);
        var otherRunner = CreateBoundSession("session-other", "runner-2", AgentSessionActivity.Unknown);
        await _store.SaveAsync(matching.Id, matching);
        await _store.SaveAsync(active.Id, active);
        await _store.SaveAsync(idle.Id, idle);
        await _store.SaveAsync(otherRunner.Id, otherRunner);

        var bindings = await _store.ListByRunnerForReconcileAsync("runner-1");

        Assert.Equal(["session-active", "session-matching"], bindings.Select(binding => binding.SessionId).Order().ToArray());
        var projected = Assert.Single(bindings, binding => binding.SessionId == matching.Id);
        Assert.Equal("opencode", projected.Runtime);
        Assert.Equal("runtime-session-matching", projected.RuntimeSessionId);
        Assert.Equal("/work/session-matching", projected.WorkDir);
    }

    private static AgentSession CreateBoundSession(string id, string runnerId, AgentSessionActivity activity)
    {
        var session = AgentSession.Create(
            id,
            runnerId,
            $"/work/{id}",
            metadata: WorkflowMetadata(),
            now: TestTime.UtcDateTime,
            runtime: "opencode");
        session.AttachPhysicalSession(
            $"runtime-{id}",
            model: null,
            workDir: $"/work/{id}",
            changeDir: null,
            processPid: null,
            now: TestTime.UtcDateTime);
        session.Status = session.Status with { Activity = activity };
        return session;
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
