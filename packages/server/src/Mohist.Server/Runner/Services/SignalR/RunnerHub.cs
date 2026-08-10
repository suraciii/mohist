using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Runner.Services.SignalR;

public class RunnerHub : Hub
{
    private readonly RunnerConnectionTracker _tracker;
    private readonly IGrainFactory _grains;
    private readonly ILogger<RunnerHub> _log;

    public RunnerHub(RunnerConnectionTracker tracker, IGrainFactory grains, ILogger<RunnerHub> log)
    {
        _tracker = tracker;
        _grains = grains;
        _log = log;
    }

    public override async Task OnConnectedAsync()
    {
        var query = Context.GetHttpContext()?.Request.Query;
        var runnerId = query?["runnerId"].ToString();
        if (string.IsNullOrEmpty(runnerId)) return;

        var connectionGeneration = _tracker.Register(runnerId, Context.ConnectionId);

        var buildGitHash = NormalizeBuildGitHash(query?["buildGitHash"].ToString());
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UpdateRuntimeIdentityAsync(
            buildGitHash,
            NormalizeBuildGitHash(query?["component"].ToString()),
            NormalizeBuildGitHash(query?["version"].ToString()),
            NormalizeBuildGitHash(query?["sourceRevision"].ToString()) ?? buildGitHash,
            NormalizeBuildGitHash(query?["treeHash"].ToString()),
            NormalizeBuildGitHash(query?["artifactDigest"].ToString()),
            NormalizeBuildGitHash(query?["releaseId"].ToString()),
            long.TryParse(query?["generation"].ToString(), out var generation) && generation > 0 ? generation : null,
            connectionGeneration);
    }

    public Task<string> Ping() => Task.FromResult(Context.ConnectionId ?? string.Empty);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var runnerId = Context.GetHttpContext()?.Request.Query["runnerId"].ToString();
        if (!string.IsNullOrEmpty(runnerId))
        {
            foreach (var sessionId in _tracker.UnregisterAndGetSessions(runnerId, Context.ConnectionId))
                _ = _grains.GetGrain<IAgentSessionGrain>(sessionId).RunnerDisconnectedAsync();
        }
        return base.OnDisconnectedAsync(exception);
    }

    private static string? NormalizeBuildGitHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }
}
