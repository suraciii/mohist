using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services;

public interface IRunnerActivityProbeCoordinator
{
    Task ProbeAsync(
        string runnerId,
        string processGeneration,
        Func<CancellationToken, Task<bool>> isCurrentConnection,
        Func<RunnerSessionActivityProbeRequest, CancellationToken, Task<RunnerSessionActivityProbeResult>> send,
        CancellationToken ct);
}

/// <summary>
/// Reconciles durable Session activity against one exact Runner control
/// connection. Connection ownership is supplied by the registry so a probe can
/// neither move to a replacement socket nor apply a reply after replacement.
/// </summary>
public sealed class RunnerActivityProbeCoordinator : IRunnerActivityProbeCoordinator, ISingletonService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<RunnerActivityProbeCoordinator> _log;

    public RunnerActivityProbeCoordinator(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<RunnerActivityProbeCoordinator> log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public async Task ProbeAsync(
        string runnerId,
        string processGeneration,
        Func<CancellationToken, Task<bool>> isCurrentConnection,
        Func<RunnerSessionActivityProbeRequest, CancellationToken, Task<RunnerSessionActivityProbeResult>> send,
        CancellationToken ct)
    {
        IReadOnlyList<string> sessionIds;
        using (var scope = _scopes.CreateScope())
        {
            sessionIds = await scope.ServiceProvider
                .GetRequiredService<IAgentSessionStore>()
                .ListSessionIdsByRunnerAsync(runnerId, ct);
        }

        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var session = _grains.GetGrain<IAgentSessionGrain>(sessionId);
                var probe = await session.PrepareActivityProbeAsync(runnerId);
                if (probe is null || !probe.HasCompleteTarget()
                    || !string.Equals(probe.RunnerId, runnerId, StringComparison.Ordinal))
                    continue;

                if (!await isCurrentConnection(ct))
                    return;

                var result = await send(probe, ct);
                if (!await isCurrentConnection(ct))
                    return;
                if (!IsCompleteMatchingResult(probe, result))
                    continue;

                await session.ApplyActivityProbeAsync(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failed, remote-error, malformed, or unanswered probe is no
                // evidence. Other Sessions on this connection remain eligible.
                _log.LogWarning(
                    ex,
                    "Runner {RunnerId} activity probe failed for AgentSession {SessionId} on process {ProcessGeneration}",
                    runnerId,
                    sessionId,
                    processGeneration);
            }
        }
    }

    private static bool IsCompleteMatchingResult(
        RunnerSessionActivityProbeRequest request,
        RunnerSessionActivityProbeResult? result) =>
        result is not null
        && result.Probe is not null
        && result.Probe.HasCompleteTarget()
        && result.Probe == request
        && result.HasKnownObservation();
}
