using CloudNative.CloudEvents;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Subscribes to <c>com.mohist.runner.disconnected</c> and marks any
/// in-flight agent sessions tied to that runner as failed. Without this,
/// a runner that crashes (process kill, TCP drop, heartbeat timeout) would
/// leave its sessions in "running" forever — the audit's primary gap.
///
/// Sessions are failed by looking up <c>AgentSessionRow</c> entries where
/// <c>RunnerId</c> matches the disconnected runner, then calling
/// <c>IAgentSessionGrain.FailIfRunningAsync</c> on each. The grain's
/// <c>FailIfRunningAsync</c> is idempotent — terminal sessions are skipped.
/// </summary>
public sealed class AgentSessionRunnerBridge : IHostedService
{
    private readonly IEventBus _bus;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<AgentSessionRunnerBridge> _log;
    private readonly List<IDisposable> _subscriptions = new();

    private static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
        "cancelled",
    };

    public AgentSessionRunnerBridge(
        IEventBus bus,
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        ILogger<AgentSessionRunnerBridge> log)
    {
        _bus = bus;
        _dbFactory = dbFactory;
        _grains = grains;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscriptions.Add(_bus.OnType(EventCatalog.ReverseDns.RunnerDisconnected, OnRunnerDisconnected));
        _log.LogInformation("AgentSessionRunnerBridge subscribed to {Event}", EventCatalog.ReverseDns.RunnerDisconnected);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }

    private async Task OnRunnerDisconnected(CloudEvent evt)
    {
        var runnerId = TryGetString(evt, "runnerid");
        if (string.IsNullOrEmpty(runnerId))
        {
            _log.LogWarning("runner_disconnected event missing runnerid extension");
            return;
        }

        var reason = TryGetString(evt, "reason") ?? "runner-disconnected";

        try
        {
            var sessionIds = await FindActiveSessionsForRunnerAsync(runnerId);
            if (sessionIds.Count == 0) return;

            _log.LogInformation("Failing {Count} sessions for disconnected runner {RunnerId} ({Reason})",
                sessionIds.Count, runnerId, reason);

            foreach (var sessionId in sessionIds)
            {
                try
                {
                    var session = _grains.GetGrain<IAgentSessionGrain>(sessionId);
                    await session.FailIfRunningAsync($"runner-disconnected:{reason}");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to mark session {SessionId} as failed for runner {RunnerId}",
                        sessionId, runnerId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AgentSessionRunnerBridge failed while processing runner_disconnected for {RunnerId}", runnerId);
        }
    }

    private async Task<List<string>> FindActiveSessionsForRunnerAsync(string runnerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.RunnerId == runnerId && !TerminalStates.Contains(s.Status))
            .Select(s => s.Id)
            .ToListAsync();
        return rows;
    }

    private static string? TryGetString(CloudEvent evt, string extensionName)
    {
        foreach (var (attr, value) in evt.GetPopulatedAttributes())
        {
            if (attr.IsExtension && attr.Name == extensionName && value is not null)
            {
                return value.ToString();
            }
        }
        return null;
    }
}
