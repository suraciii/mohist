using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services;

public sealed class RunnerSessionCommandDispatcher : ISessionCommandDispatcher
{
    private readonly IRunnerControlTransport _control;
    private readonly ILogger<RunnerSessionCommandDispatcher> _log;

    public RunnerSessionCommandDispatcher(
        IRunnerControlTransport control,
        ILogger<RunnerSessionCommandDispatcher> log)
    {
        _control = control;
        _log = log;
    }

    public async Task<SessionCommandResult> DispatchAsync(
        SessionCommandRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return await _control.SendRequestAsync<SessionCommandRequest, SessionCommandResult>(
                request.RunnerId,
                "session.command",
                request,
                ct: ct);
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
