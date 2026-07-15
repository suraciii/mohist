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

    public RunnerSessionCommandDispatcher(
        IHubContext<RunnerHub> hub,
        RunnerConnectionTracker connections,
        ILogger<RunnerSessionCommandDispatcher> log)
    {
        _hub = hub;
        _connections = connections;
        _log = log;
    }

    public async Task<SessionCommandResult> DispatchAsync(
        SessionCommandRequest request,
        CancellationToken ct = default)
    {
        var connectionId = _connections.GetConnectionId(request.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId))
            return Unavailable();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            return await _hub.Clients.Client(connectionId).InvokeAsync<SessionCommandResult?>(
                HubMethod,
                request,
                timeout.Token) ?? Unavailable();
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
}
