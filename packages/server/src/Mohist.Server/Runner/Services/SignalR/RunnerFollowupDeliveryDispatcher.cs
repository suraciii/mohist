using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services.SignalR;

public sealed class RunnerFollowupDeliveryDispatcher : IFollowupDeliveryDispatcher
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IHubContext<RunnerHub> _runnerHub;
    private readonly RunnerConnectionTracker _connections;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunnerFollowupDeliveryDispatcher> _log;

    public RunnerFollowupDeliveryDispatcher(
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections,
        TimeProvider timeProvider,
        ILogger<RunnerFollowupDeliveryDispatcher> log)
    {
        _runnerHub = runnerHub;
        _connections = connections;
        _timeProvider = timeProvider;
        _log = log;
    }

    public async Task<FollowupDeliveryResult> DispatchAsync(FollowupDeliveryRequest request, CancellationToken ct = default)
    {
        var connectionId = _connections.GetConnectionId(request.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
            return new FollowupDeliveryResult(false);

        var binding = new RunnerSessionBinding(
            request.Runtime,
            request.RuntimeSessionId,
            request.RunnerId,
            request.WorkDir);
        var target = string.Equals(request.SourceKind, "workflow", StringComparison.Ordinal)
            ? new RunnerSessionTarget(
                "workflow",
                request.ProjectId,
                binding,
                WorkflowRunId: request.WorkflowRunId,
                SessionName: request.SessionName,
                SessionId: request.SessionId)
            : new RunnerSessionTarget(
                "generic",
                request.ProjectId,
                binding,
                SessionId: request.SessionId,
                Definition: request.Definition);
        var payload = new FollowupParams(
            target,
            string.Join("\n", request.InputTexts),
            request.OperationId,
            request.InputId,
            request.TurnId!,
            request.SlackExecutionContext,
            request.Attachments is { Count: > 0 }
                ? request.Attachments
                    .Select(descriptor => new FollowupAttachmentDescriptor(
                        descriptor.Id,
                        descriptor.OriginalFileName,
                        descriptor.ContentType,
                        descriptor.Size))
                    .ToArray()
                : null);

        try
        {
            using var timeoutCancellation = new CancellationTokenSource();
            var timeout = Task.Delay(RequestTimeout, _timeProvider, timeoutCancellation.Token);
            var invocation = _runnerHub.Clients.Client(connectionId).InvokeAsync<RunnerFollowupDeliveryResult?>(
                "ReceiveFollowup",
                payload,
                ct);
            var response = invocation.WaitAsync(ct);
            if (await Task.WhenAny(response, timeout) == timeout)
                return new FollowupDeliveryResult(false);

            timeoutCancellation.Cancel();
            return new FollowupDeliveryResult((await response)?.Accepted == true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Runner {RunnerId} failed to receive follow-up for AgentSession {SessionId}",
                request.RunnerId,
                request.SessionId);
            return new FollowupDeliveryResult(false);
        }
    }
}
