using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Runner.Subscriptions;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Subscriptions;

public class RunnerWorkflowTerminalStatusHandlerSpecs
{
    [Fact]
    public async Task HandleAsync_RouterThrows_ExceptionPropagates()
    {
        // issue-363 T-002: the handler awaits the router call on the
        // dispatch stack. Any exception escaping RouteAsync must reach
        // the durable dispatcher so it can retry/dead-letter the event
        // for this handler — the handler MUST NOT detach the router
        // invocation or swallow the exception.
        var observed = new List<string>();
        var router = new ThrowingStatusRouter(observed, new InvalidOperationException("router unavailable"));
        var handler = new RunnerWorkflowTerminalStatusHandler(router, NullLogger<RunnerWorkflowTerminalStatusHandler>.Instance);

        var evt = BuildTerminalEvent("wr_propagate", EventCatalog.ReverseDns.WorkflowRunCompleted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(evt, CancellationToken.None));
        Assert.Equal("router unavailable", ex.Message);
        Assert.Equal(new[] { "wr_propagate:Completed" }, observed);
    }

    [Fact]
    public async Task HandleAsync_UnresolvedSource_EnvelopesNoOpWithoutCallingRouter()
    {
        // An envelope whose source does not parse to a workflow run id
        // is a valid no-op — the handler returns a completed task
        // without invoking the router, so the dispatcher treats it as
        // delivered for this handler.
        var observed = new List<string>();
        var router = new RecordingStatusRouter(observed);
        var handler = new RunnerWorkflowTerminalStatusHandler(router, NullLogger<RunnerWorkflowTerminalStatusHandler>.Instance);

        var evt = new CloudEvent(
            id: "evt_unresolved",
            source: new Uri("/mohist/workflow-runs/", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: DateTimeOffset.UnixEpoch,
            data: null);

        await handler.HandleAsync(evt, CancellationToken.None);
        Assert.Empty(observed);
    }

    private static CloudEvent BuildTerminalEvent(string workflowRunId, string type) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: type,
            time: DateTimeOffset.UnixEpoch,
            data: null,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.WorkflowRunId] = workflowRunId,
            });

    private sealed class RecordingStatusRouter : IRunnerWorkflowStatusRouter
    {
        private readonly List<string> _calls;

        public RecordingStatusRouter(List<string> calls) => _calls = calls;

        public Task RouteAsync(string workflowRunId, WorkflowRunStatus status, CancellationToken ct = default)
        {
            _calls.Add($"{workflowRunId}:{status}");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStatusRouter : IRunnerWorkflowStatusRouter
    {
        private readonly List<string> _calls;
        private readonly Exception _throw;

        public ThrowingStatusRouter(List<string> calls, Exception @throw)
        {
            _calls = calls;
            _throw = @throw;
        }

        public Task RouteAsync(string workflowRunId, WorkflowRunStatus status, CancellationToken ct = default)
        {
            _calls.Add($"{workflowRunId}:{status}");
            return Task.FromException(_throw);
        }
    }
}
