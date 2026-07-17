using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services.SignalR;

public sealed class RunnerSessionCommandDispatcher : ISessionCommandDispatcher
{
    private const string HubMethod = "SessionCommand";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IHubContext<RunnerHub> _hub;
    private readonly RunnerConnectionTracker _connections;
    private readonly ILogger<RunnerSessionCommandDispatcher> _log;
    private readonly TimeProvider _timeProvider;

    public RunnerSessionCommandDispatcher(
        IHubContext<RunnerHub> hub,
        RunnerConnectionTracker connections,
        ILogger<RunnerSessionCommandDispatcher> log,
        TimeProvider timeProvider)
    {
        _hub = hub;
        _connections = connections;
        _log = log;
        _timeProvider = timeProvider;
    }

    public async Task<SessionCommandResult> DispatchAsync(
        SessionCommandRequest request,
        CancellationToken ct = default)
    {
        var connectionId = _connections.GetConnectionId(request.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
            return NotStarted();

        try
        {
            using var timeoutCancellation = new CancellationTokenSource();
            var timeout = Task.Delay(RequestTimeout, _timeProvider, timeoutCancellation.Token);
            var invocation = _hub.Clients.Client(connectionId).InvokeAsync<SessionCommandResult?>(
                HubMethod,
                request,
                ct);
            var response = invocation.WaitAsync(ct);

            if (await Task.WhenAny(response, timeout) == timeout)
                return Unavailable();

            timeoutCancellation.Cancel();
            return await response ?? Unavailable();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Runner {RunnerId} failed to handle {Command} for AgentSession {SessionId}",
                request.RunnerId,
                request.Command,
                request.SessionId);
            return Unavailable();
        }
    }

    private static SessionCommandResult Unavailable() =>
        new(false, Error: SessionCommandError.Unavailable);

    private static SessionCommandResult NotStarted() =>
        new(false, Error: SessionCommandError.NotStarted);
}
