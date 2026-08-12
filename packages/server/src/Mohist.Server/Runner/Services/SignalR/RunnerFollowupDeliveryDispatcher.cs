using Microsoft.AspNetCore.SignalR;
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

        object binding = new
        {
            runtime = request.Runtime,
            runtimeSessionId = request.RuntimeSessionId,
            runnerId = request.RunnerId,
            workDir = request.WorkDir,
        };
        object target = string.Equals(request.SourceKind, "workflow", StringComparison.Ordinal)
            ? new
            {
                kind = "workflow",
                projectId = request.ProjectId,
                workflowRunId = request.WorkflowRunId,
                sessionName = request.SessionName,
                sessionId = request.SessionId,
                binding,
            }
            : new
            {
                kind = "generic",
                projectId = request.ProjectId,
                sessionId = request.SessionId,
                definition = request.Definition,
                binding,
            };
        var payload = new
        {
            target,
            text = string.Join("\n", request.InputTexts),
            operationId = request.OperationId,
            inputId = request.InputId,
            turnId = request.TurnId,
            slackExecutionContext = request.SlackExecutionContext,
            attachments = request.Attachments is { Count: > 0 }
                ? request.Attachments
                    .Select(descriptor => new
                    {
                        id = descriptor.Id,
                        name = descriptor.OriginalFileName,
                        contentType = descriptor.ContentType,
                        size = descriptor.Size,
                    })
                    .ToArray()
                : null,
        };

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

public sealed record RunnerFollowupDeliveryResult(bool Accepted, string? Error = null);
