using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.SignalR;

public sealed class RecordingRunnerHubContextSpecs
{
    [Fact]
    public async Task Owners_RecordOnlyTheirOwnInvocationsAndResponses_WhenInvokedConcurrently()
    {
        var hub = new RecordingRunnerHubContext();
        using var first = hub.CreateOwner("connection-first");
        using var second = hub.CreateOwner("connection-second");
        var firstResponse = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        first.SetInvocationResponseFactory("SessionCommand", _ => firstResponse.Task);
        second.SetInvocationResponseFactory("SessionCommand", _ => secondResponse.Task);

        var firstTask = InvokeAsync(hub, "connection-first", "session-first");
        var secondTask = InvokeAsync(hub, "connection-second", "session-second");

        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
        firstResponse.SetResult(new SessionCommandResult(true, RuntimeSessionId: "first-replacement"));
        secondResponse.SetResult(new SessionCommandResult(true, RuntimeSessionId: "second-replacement"));

        Assert.Equal("first-replacement", (await firstTask).RuntimeSessionId);
        Assert.Equal("second-replacement", (await secondTask).RuntimeSessionId);
        Assert.Equal("connection-first", Assert.Single(first.Invocations).ConnectionId);
        Assert.Equal("session-first", Assert.IsType<SessionCommandRequest>(Assert.Single(first.Invocations).Arguments.Single()).SessionId);
        Assert.Equal("connection-second", Assert.Single(second.Invocations).ConnectionId);
        Assert.Equal("session-second", Assert.IsType<SessionCommandRequest>(Assert.Single(second.Invocations).Arguments.Single()).SessionId);
        Assert.Empty(hub.Invocations);
        Assert.Equal(2, hub.OwnerCount);
    }

    [Fact]
    public async Task ProxyCreatedBeforeOwner_BindsOnceWithoutFallingBackToGlobalState()
    {
        var hub = new RecordingRunnerHubContext();
        var proxy = Client(hub, "connection-owned");
        hub.SetInvocationResponse("SessionCommand", new SessionCommandResult(true, RuntimeSessionId: "global-response"));
        using var owner = hub.CreateOwner("connection-owned");
        owner.SetInvocationResponse("SessionCommand", new SessionCommandResult(true, RuntimeSessionId: "owner-response"));

        var first = await proxy.InvokeCoreAsync<SessionCommandResult>(
            "SessionCommand",
            [CreateRequest("session-first")],
            CancellationToken.None);

        Assert.Equal("owner-response", first.RuntimeSessionId);
        Assert.Single(owner.Invocations);
        Assert.Empty(hub.Invocations);
    }

    [Fact]
    public async Task DisposingOwner_CancelsPendingInvocationAndReleasesOnlyThatOwner()
    {
        var hub = new RecordingRunnerHubContext();
        var pendingOwner = hub.CreateOwner("connection-pending");
        using var sibling = hub.CreateOwner("connection-sibling");
        var pendingResponse = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOwner.SetInvocationResponseFactory("SessionCommand", _ => pendingResponse.Task);
        sibling.SetInvocationResponse("SessionCommand", new SessionCommandResult(true, RuntimeSessionId: "sibling-replacement"));

        var pending = Client(hub, "connection-pending").InvokeCoreAsync<SessionCommandResult>(
            "SessionCommand",
            [CreateRequest("session-pending")],
            CancellationToken.None);

        Assert.False(pending.IsCompleted);
        pendingOwner.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, hub.OwnerCount);
        Assert.Empty(pendingOwner.Invocations);

        var siblingResult = await Client(hub, "connection-sibling")
            .InvokeCoreAsync<SessionCommandResult>("SessionCommand", [CreateRequest("session-sibling")], CancellationToken.None);
        Assert.Equal("sibling-replacement", siblingResult.RuntimeSessionId);
        Assert.Single(sibling.Invocations);
    }

    [Fact]
    public async Task ReleaseAfterInvokeRecordBoundary_CancelsInvokeWithoutGlobalFallback()
    {
        var messageRecorded = NewSignal();
        var continueProxy = NewSignal();
        var hub = new RecordingRunnerHubContext(afterOwnerMessageRecorded: () =>
        {
            messageRecorded.TrySetResult();
            continueProxy.Task.GetAwaiter().GetResult();
        });
        hub.SetInvocationResponse("SessionCommand", new SessionCommandResult(true, RuntimeSessionId: "global-response"));
        var owner = hub.CreateOwner("connection-owned");
        owner.SetInvocationResponse("SessionCommand", new SessionCommandResult(true, RuntimeSessionId: "owner-response"));
        var invocation = Task.Run(() => Client(hub, "connection-owned").InvokeCoreAsync<SessionCommandResult>(
            "SessionCommand",
            [CreateRequest("session-closing")],
            CancellationToken.None));

        await messageRecorded.Task;
        try
        {
            await Task.Run(owner.Dispose);
        }
        finally
        {
            continueProxy.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Empty(owner.SentMessages);
        Assert.Empty(owner.Invocations);
        Assert.Empty(hub.SentMessages);
        Assert.Empty(hub.Invocations);
        Assert.Equal(0, hub.OwnerCount);
    }

    [Fact]
    public async Task ReleaseDuringCancellationRegistration_DefersTokenDisposalAndCancelsInvocation()
    {
        var registrationLeaseAcquired = NewSignal();
        var continueRegistration = NewSignal();
        var cancellationDisposed = NewSignal();
        var hub = new RecordingRunnerHubContext(
            afterOwnerCancellationRegistrationLeaseAcquired: () =>
            {
                registrationLeaseAcquired.TrySetResult();
                continueRegistration.Task.GetAwaiter().GetResult();
            },
            afterOwnerCancellationDisposed: () => cancellationDisposed.TrySetResult());
        var owner = hub.CreateOwner("connection-owned");
        var pendingResponse = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.SetInvocationResponseFactory("SessionCommand", _ => pendingResponse.Task);
        var invocation = Task.Run(() => Client(hub, "connection-owned").InvokeCoreAsync<SessionCommandResult>(
            "SessionCommand",
            [CreateRequest("session-owned")],
            CancellationToken.None));

        await registrationLeaseAcquired.Task;
        try
        {
            await Task.Run(owner.Dispose);
            Assert.False(cancellationDisposed.Task.IsCompleted);
        }
        finally
        {
            continueRegistration.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        await cancellationDisposed.Task;
        Assert.Empty(owner.SentMessages);
        Assert.Empty(owner.Invocations);
        Assert.Equal(0, hub.OwnerCount);
    }

    private static async Task<SessionCommandResult> InvokeAsync(
        RecordingRunnerHubContext hub,
        string connectionId,
        string sessionId) =>
        await Client(hub, connectionId)
            .InvokeCoreAsync<SessionCommandResult>("SessionCommand", [CreateRequest(sessionId)], CancellationToken.None);

    private static ISingleClientProxy Client(RecordingRunnerHubContext hub, string connectionId) =>
        ((IHubClients)hub.Clients).Client(connectionId);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SessionCommandRequest CreateRequest(string sessionId) => new(
        SessionId: sessionId,
        Runtime: "opencode",
        RuntimeSessionId: sessionId,
        RunnerId: $"runner-{sessionId}",
        WorkDir: "/virtual/workspace",
        Command: SessionCommandKind.Reset);
}
