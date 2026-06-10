using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

[Subscription(Type = "com.mohist.runner.disconnected")]
public sealed class AgentSessionRunnerBridge : ICloudEventHandler
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<AgentSessionRunnerBridge> _log;

    private static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
        "cancelled",
    };

    public AgentSessionRunnerBridge(
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        ILogger<AgentSessionRunnerBridge> log)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => true;

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!evt.Extensions.TryGetValue("runnerid", out var runnerId) || string.IsNullOrEmpty(runnerId))
        {
            _log.LogWarning("runner_disconnected event missing runnerid extension");
            return;
        }

        var reason = evt.Extensions.TryGetValue("reason", out var r) && r is not null
            ? r
            : "runner-disconnected";

        try
        {
            var sessionIds = await FindActiveSessionsForRunnerAsync(runnerId, ct);
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

    private async Task<List<string>> FindActiveSessionsForRunnerAsync(string runnerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.RunnerId == runnerId && !TerminalStates.Contains(s.Status))
            .Select(s => s.Id)
            .ToListAsync(ct);
        return rows;
    }
}
