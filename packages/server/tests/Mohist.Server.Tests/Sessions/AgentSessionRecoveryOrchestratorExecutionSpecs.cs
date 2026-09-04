using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

public sealed partial class AgentSessionRecoveryOrchestratorSpecs
{
    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_DisabledBindingRuntimeReturnsStableApiError(SessionCommandKind command)
    {
        var (_, sessionId) = await CreateIdleSessionAsync($"runtime-disabled-{command.ToString().ToLowerInvariant()}");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.Enqueue(new SessionCommandResult(Ok: false, Error: SessionCommandError.RuntimeUnavailable));

        var result = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: null, dispatcher);

        Assert.Equal(503, result.Status);
        Assert.Equal("runtime_unavailable", result.Body.GetProperty("code").GetString());
        Assert.Single(dispatcher.Requests);
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_SimulatedRunnerRestart_AppliesOperationIdAtMostOnceAndAllowsNewOperation(SessionCommandKind command)
    {
        var (_, sessionId) = await CreateIdleSessionAsync($"runtime-unavailable-{command.ToString().ToLowerInvariant()}");
        var dispatcher = new RecordingSessionCommandDispatcher();
        dispatcher.Enqueue(new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable));
        dispatcher.EnqueueSuccess();

        var first = await ExecuteRecoveryAsync(command, sessionId, "restart-operation", dispatcher);
        var replay = await ExecuteRecoveryAsync(command, sessionId, "restart-operation", dispatcher);
        var replacement = await ExecuteRecoveryAsync(command, sessionId, "new-operation", dispatcher);

        Assert.Equal(503, first.Status);
        Assert.Equal(503, replay.Status);
        Assert.Equal("runner_unavailable", replay.Body.GetProperty("code").GetString());
        Assert.Equal(200, replacement.Status);
        Assert.Equal(2, dispatcher.Requests.Count);
        Assert.NotEqual(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
        await AssertRuntimeBindingAsync(sessionId, command, dispatcher.Requests[1]);
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_AdmittedInvalidResultFailsClosedWithoutRedelivery(SessionCommandKind command)
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
        Assert.Equal(503, retry.Status);
        Assert.Equal("runner_unavailable", retry.Body.GetProperty("code").GetString());
        Assert.Single(dispatcher.Requests);

        var session = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        var admission = Assert.Single(session.Status.SessionCommandAdmissionFacts!);
        Assert.Null(admission.Outcome);
        Assert.Null(session.Status.PendingReset);
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task RecoveryCommand_CancelledRequestFailsAdmittedOperationBeforeNewRetry(SessionCommandKind command)
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
        var first = ExecuteRecoveryAsync(command, sessionId, idempotencyKey: "cancelled-operation", dispatcher, cancellation.Token);
        await dispatched.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var replay = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: "cancelled-operation", dispatcher);
        var retry = await ExecuteRecoveryAsync(command, sessionId, idempotencyKey: "replacement-operation", dispatcher);

        Assert.Equal(503, replay.Status);
        Assert.Equal(200, retry.Status);
        Assert.Equal(2, dispatcher.Requests.Count);
        Assert.NotEqual(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);
    }

    [Fact]
    public async Task RunnerGenerationReplacementKeepsOldIdentityUnavailableAndDispatchesNewIdentityOnce()
    {
        var (_, sessionId) = await CreateIdleSessionAsync("runtime-generation-race");
        var dispatcher = new RecordingSessionCommandDispatcher();
        var dispatchStarted = new TaskCompletionSource<SessionCommandRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Enqueue((request, _) =>
        {
            dispatchStarted.TrySetResult(request);
            return oldResult.Task;
        });
        dispatcher.EnqueueSuccess();

        var first = ExecuteRecoveryAsync(SessionCommandKind.Compact, sessionId, "old-key", dispatcher);
        var oldRequest = await dispatchStarted.Task;
        var admitted = (await LoadAsync(sessionId))!.Status.PendingReset;
        Assert.NotNull(admitted);
        Assert.True(admitted!.EffectAdmitted);
        Assert.Equal(oldRequest.OperationId, admitted.OperationId);
        Assert.Single(dispatcher.Requests);

        dispatcher.ProcessGeneration = "replacement-generation";
        oldResult.SetResult(new SessionCommandResult(Ok: true));

        var refused = await first;
        var replay = await ExecuteRecoveryAsync(SessionCommandKind.Compact, sessionId, "old-key", dispatcher);
        var replacement = await ExecuteRecoveryAsync(SessionCommandKind.Compact, sessionId, "new-key", dispatcher);

        Assert.Equal(503, refused.Status);
        Assert.Equal(503, replay.Status);
        Assert.Equal("runner_unavailable", replay.Body.GetProperty("code").GetString());
        Assert.Equal(200, replacement.Status);
        Assert.Equal(2, dispatcher.Requests.Count);
        Assert.Equal("test-generation", dispatcher.Requests[0].ProcessGeneration);
        Assert.Equal("replacement-generation", dispatcher.Requests[1].ProcessGeneration);
        Assert.NotEqual(dispatcher.Requests[0].OperationId, dispatcher.Requests[1].OperationId);

        var staleCompletion = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).CompleteCompactAsync(
            new CompleteCompactAgentSessionCommand(oldRequest.OperationId, "test-generation", Summary: "stale"));
        await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() => staleCompletion);
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
}
