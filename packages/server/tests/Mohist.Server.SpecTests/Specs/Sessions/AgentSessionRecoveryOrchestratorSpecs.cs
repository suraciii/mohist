using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionRecoveryOrchestratorSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionRecoveryOrchestratorSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_UnavailableRunnerResult_RetriesPersistedOperation(SessionCommandKind command)
    {
        var (_, sessionId) = await CreateIdleSessionAsync($"runtime-unavailable-{command.ToString().ToLowerInvariant()}");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.Enqueue(new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable));
        dispatcher.EnqueueSuccess();

        var first = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: null, dispatcher);
        var retry = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: null, dispatcher);

        Assert.Equal(503, first.Status);
        Assert.Equal("runner_unavailable", first.Body.GetProperty("code").GetString());
        Assert.Equal(200, retry.Status);
        Assert.Equal(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
        await AssertRuntimeBindingAsync(sessionId, command, dispatcher.Requests[1]);
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_InvalidRunnerResult_RetriesPersistedOperation(SessionCommandKind command)
    {
        var (_, sessionId) = await CreateIdleSessionAsync($"runtime-invalid-{command.ToString().ToLowerInvariant()}");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.Enqueue(request => Task.FromResult(command == SessionCommandKind.Compact
            ? new SessionCommandResult(Ok: true, RuntimeSessionId: "unexpected-runtime")
            : new SessionCommandResult(Ok: true)));
        dispatcher.EnqueueSuccess();

        var first = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: "invalid-key", dispatcher);
        var retry = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: "invalid-key", dispatcher);

        Assert.Equal(502, first.Status);
        Assert.Equal("runner_invalid_response", first.Body.GetProperty("code").GetString());
        Assert.Equal(200, retry.Status);
        Assert.Equal(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
        await AssertRuntimeBindingAsync(sessionId, command, dispatcher.Requests[1]);
    }

    [Fact]
    public async Task Reset_NewIdempotencyKeyJoinsPendingOperationAndReplaysCompletion()
    {
        var (_, sessionId) = await CreateIdleSessionAsync("runtime-reset-join");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.Enqueue(new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable));
        dispatcher.EnqueueSuccess();

        var first = await ExecuteRecoveryAsync(SessionCommandKind.Reset, sessionId, "reset-1", dispatcher);
        var joined = await ExecuteRecoveryAsync(SessionCommandKind.Reset, sessionId, "reset-2", dispatcher);
        var replay = await ExecuteRecoveryAsync(SessionCommandKind.Reset, sessionId, "reset-2", dispatcher);

        Assert.Equal(503, first.Status);
        Assert.Equal(200, joined.Status);
        Assert.Equal(200, replay.Status);
        Assert.Equal(2, dispatcher.Requests.Count);
        Assert.Equal(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
        Assert.Equal(
            joined.Body.GetProperty("data").GetProperty("id").GetString(),
            replay.Body.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("runtime-reset-join-replacement", (await LoadAsync(sessionId))!.Status.AgentRuntimeSessionId);
    }

    [Fact]
    public async Task Reset_CommandNotStartedAbandonsPendingOperationAndAllowsNewOne()
    {
        var (_, sessionId) = await CreateIdleSessionAsync("runtime-not-started");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.Enqueue(new SessionCommandResult(Ok: false, Error: SessionCommandError.NotStarted));
        dispatcher.EnqueueSuccess();

        var first = await ExecuteRecoveryAsync(SessionCommandKind.Reset, sessionId, idempotencyKey: null, dispatcher);
        var retry = await ExecuteRecoveryAsync(SessionCommandKind.Reset, sessionId, idempotencyKey: null, dispatcher);

        Assert.Equal(503, first.Status);
        Assert.Equal("runner_command_not_started", first.Body.GetProperty("code").GetString());
        Assert.Equal(200, retry.Status);
        Assert.NotEqual(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_OmittedIdempotencyKeyDoesNotReplayCompletedOperation(SessionCommandKind command)
    {
        var (_, sessionId) = await CreateIdleSessionAsync($"runtime-omitted-{command.ToString().ToLowerInvariant()}");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.EnqueueSuccess();
        dispatcher.EnqueueSuccess();

        var first = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: null, dispatcher);
        var second = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: null, dispatcher);

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(2, dispatcher.Requests.Count);
        Assert.NotEqual(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
    }

    [Fact]
    public async Task Compact_ExplicitLegacyIdempotencyKeyDoesNotMergeWithOmittedKey()
    {
        var (_, sessionId) = await CreateIdleSessionAsync("runtime-explicit-legacy");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.EnqueueSuccess();
        dispatcher.EnqueueSuccess();

        var explicitLegacy = await ExecuteRecoveryAsync(SessionCommandKind.Compact, sessionId, "legacy", dispatcher);
        var omitted = await ExecuteRecoveryAsync(SessionCommandKind.Compact, sessionId, idempotencyKey: null, dispatcher);

        Assert.Equal(200, explicitLegacy.Status);
        Assert.Equal(200, omitted.Status);
        Assert.Equal(2, dispatcher.Requests.Count);
        Assert.NotEqual(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_CancelledRequestKeepsPendingOperationForRetry(SessionCommandKind command)
    {
        var (_, sessionId) = await CreateIdleSessionAsync($"runtime-cancelled-{command.ToString().ToLowerInvariant()}");
        var dispatcher = new RecordingSessionCommandDispatcher();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Enqueue(async (_, ct) =>
        {
            dispatched.TrySetResult();
            var pending = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => pending.TrySetCanceled(ct));
            return await pending.Task;
        });
        dispatcher.EnqueueSuccess();

        using var cancellation = new CancellationTokenSource();
        var first = ExecuteRecoveryAsync(command, sessionId, idempotencyKey: null, dispatcher, cancellation.Token);
        await dispatched.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var retry = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: null, dispatcher);

        Assert.Equal(200, retry.Status);
        Assert.Equal(2, dispatcher.Requests.Count);
        Assert.Equal(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
    }

    [Fact]
    public async Task Compact_WhileResetDispatchIsInFlightReturnsRecoveryInProgressWithoutMutation()
    {
        var (_, sessionId) = await CreateIdleSessionAsync("runtime-overlap");
        var dispatcher = new RecordingSessionCommandDispatcher();
        var resetStarted = new TaskCompletionSource<SessionCommandRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeReset = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Enqueue((request, _) =>
        {
            resetStarted.TrySetResult(request);
            return completeReset.Task;
        });

        var resetTask = ExecuteRecoveryAsync(SessionCommandKind.Reset, sessionId, idempotencyKey: null, dispatcher);
        var resetRequest = await resetStarted.Task;

        var compact = await ExecuteRecoveryAsync(SessionCommandKind.Compact, sessionId, idempotencyKey: null, dispatcher);

        Assert.Equal(409, compact.Status);
        Assert.Equal("recovery_in_progress", compact.Body.GetProperty("code").GetString());
        Assert.Equal("reset", compact.Body.GetProperty("details").GetProperty("operation").GetString());
        Assert.Equal("runtime-overlap", (await LoadAsync(sessionId))!.Status.AgentRuntimeSessionId);
        Assert.Single(dispatcher.Requests);

        completeReset.SetResult(new SessionCommandResult(
            Ok: true,
            RuntimeSessionId: $"{resetRequest.RuntimeSessionId}-replacement"));
        var reset = await resetTask;

        Assert.Equal(200, reset.Status);
        Assert.Equal("runtime-overlap-replacement", (await LoadAsync(sessionId))!.Status.AgentRuntimeSessionId);
    }

    [Fact]
    public async Task RunnerConflictAndMissingMapToIdleBoundaryConflictsWithoutMutation()
    {
        var (_, compactSessionId) = await CreateIdleSessionAsync("runtime-handler-conflict");
        var compactDispatcher = new RecordingSessionCommandDispatcher();
        compactDispatcher.Enqueue(new SessionCommandResult(Ok: false, Error: SessionCommandError.Conflict));

        var compact = await ExecuteRecoveryAsync(SessionCommandKind.Compact, compactSessionId, idempotencyKey: null, compactDispatcher);

        Assert.Equal(409, compact.Status);
        Assert.Equal("session_active", compact.Body.GetProperty("code").GetString());
        Assert.Equal("runtime-handler-conflict", (await LoadAsync(compactSessionId))!.Status.AgentRuntimeSessionId);

        var (_, resetSessionId) = await CreateIdleSessionAsync("runtime-handler-missing");
        var resetDispatcher = new RecordingSessionCommandDispatcher();
        resetDispatcher.Enqueue(new SessionCommandResult(Ok: false, Error: SessionCommandError.Missing));

        var reset = await ExecuteRecoveryAsync(SessionCommandKind.Reset, resetSessionId, idempotencyKey: null, resetDispatcher);

        Assert.Equal(409, reset.Status);
        Assert.Equal("runtime_session_missing", reset.Body.GetProperty("code").GetString());
        Assert.Equal("runtime-handler-missing", (await LoadAsync(resetSessionId))!.Status.AgentRuntimeSessionId);
    }

    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateIdleSessionAsync(string runtimeSessionId)
    {
        var sessionId = $"recovery-orchestrator-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, "project-1")
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
                .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, "workflow-1")
                .WithLabel(AgentSessionQueryMetadataKeys.SessionName, "build")));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId, WorkDir: "/work"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        return (grain, sessionId);
    }

    private Task<AgentSession?> LoadAsync(string sessionId) => _fixture.StateStore.LoadAsync(sessionId);

    private async Task AssertRuntimeBindingAsync(
        string sessionId,
        SessionCommandKind command,
        SessionCommandRequest request)
    {
        var state = await LoadAsync(sessionId) ?? throw new InvalidOperationException($"Missing session {sessionId}.");
        var expected = command == SessionCommandKind.Compact
            ? request.RuntimeSessionId
            : $"{request.RuntimeSessionId}-replacement";
        Assert.Equal(expected, state.Status.AgentRuntimeSessionId);
    }

    private Task<ExecutedResult> ExecuteRecoveryAsync(
        SessionCommandKind command,
        string sessionId,
        string? idempotencyKey,
        RecordingSessionCommandDispatcher dispatcher,
        CancellationToken ct = default) => command switch
        {
            SessionCommandKind.Compact => ExecuteResultAsync(AgentSessionRecoveryRoutes.ExecuteCompactAsync(
                sessionId,
                idempotencyKey,
                _fixture.Grains,
                dispatcher,
                ct)),
            SessionCommandKind.Reset => ExecuteResultAsync(AgentSessionRecoveryRoutes.ExecuteResetAsync(
                sessionId,
                idempotencyKey,
                _fixture.Grains,
                dispatcher,
                ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };

    private static async Task<ExecutedResult> ExecuteResultAsync(Task<IResult> resultTask)
    {
        var result = await resultTask;
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                foreach (var converter in JSON.Options.Converters)
                    options.SerializerOptions.Converters.Add(converter);
                options.SerializerOptions.DefaultIgnoreCondition = JSON.Options.DefaultIgnoreCondition;
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return new ExecutedResult(context.Response.StatusCode, body);
    }

    private sealed record ExecutedResult(int Status, JsonElement Body);

    private sealed class RecordingSessionCommandDispatcher : ISessionCommandDispatcher
    {
        private readonly Queue<Func<SessionCommandRequest, CancellationToken, Task<SessionCommandResult>>> _responses = [];

        public List<SessionCommandRequest> Requests { get; } = [];

        public void Enqueue(SessionCommandResult result) =>
            _responses.Enqueue((_, _) => Task.FromResult(result));

        public void Enqueue(Func<SessionCommandRequest, Task<SessionCommandResult>> response) =>
            _responses.Enqueue((request, _) => response(request));

        public void Enqueue(Func<SessionCommandRequest, CancellationToken, Task<SessionCommandResult>> response) =>
            _responses.Enqueue(response);

        public void EnqueueSuccess() => Enqueue(request => Task.FromResult(SuccessFor(request)));

        public Task<SessionCommandResult> DispatchAsync(SessionCommandRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var response = _responses.Count == 0
                ? (_, _) => Task.FromResult(SuccessFor(request))
                : _responses.Dequeue();
            return response(request, ct);
        }

        private static SessionCommandResult SuccessFor(SessionCommandRequest request) => request.Command switch
        {
            SessionCommandKind.Compact => new SessionCommandResult(Ok: true),
            SessionCommandKind.Reset => new SessionCommandResult(
                Ok: true,
                RuntimeSessionId: $"{request.RuntimeSessionId}-replacement"),
            _ => new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable),
        };
    }
}
