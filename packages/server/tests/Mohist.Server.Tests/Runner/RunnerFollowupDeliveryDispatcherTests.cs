using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Contracts;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.Tests.Runner;

[Trait("level", "L0")]
public sealed class RunnerFollowupDeliveryDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_PreservesRuntimeUnavailableError()
    {
        var transport = new RecordingTransport(new RunnerFollowupDeliveryResult(
            Accepted: false,
            Error: "runtime-unavailable"));
        var dispatcher = new RunnerFollowupDeliveryDispatcher(
            transport,
            NullLogger<RunnerFollowupDeliveryDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(new FollowupDeliveryRequest(
            ProjectId: "project-1",
            SessionId: "session-1",
            SourceKind: "generic",
            WorkflowRunId: null,
            SessionName: null,
            RunnerId: "runner-1",
            Runtime: "opencode",
            RuntimeSessionId: "runtime-session-1",
            WorkDir: "/work/session-1",
            Definition: null,
            OperationId: "operation-1",
            InputTexts: ["continue"],
            TurnId: "turn-1",
            RequiresBindingRecovery: true));

        Assert.False(result.Accepted);
        Assert.Equal("runtime-unavailable", result.Error);
        Assert.Equal("runner-1", transport.RunnerId);
        Assert.Equal("session.followup", transport.Method);
        Assert.True(transport.Payload!.RequiresBindingRecovery);
        Assert.Equal("runtime-session-1", transport.Payload.Target.Binding.RuntimeSessionId);
    }

    private sealed class RecordingTransport(RunnerFollowupDeliveryResult response) : IRunnerControlTransport
    {
        public string? RunnerId { get; private set; }
        public string? Method { get; private set; }
        public FollowupParams? Payload { get; private set; }

        public bool IsConnected(string runnerId) => true;

        public Task<TResult> SendRequestAsync<TParams, TResult>(
            string runnerId,
            string method,
            TParams parameters,
            Action? requestEnqueued = null,
            CancellationToken ct = default)
        {
            RunnerId = runnerId;
            Method = method;
            Payload = (FollowupParams)(object)parameters!;
            return Task.FromResult((TResult)(object)response);
        }
    }
}
