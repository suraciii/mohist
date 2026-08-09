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

        var buildGitHash = NormalizeBuildGitHash(query?["buildGitHash"].ToString());
        var runtimeGeneration = NormalizeBuildGitHash(query?["runtimeGeneration"].ToString());
        var artifactDigest = NormalizeBuildGitHash(query?["artifactDigest"].ToString());
        var runtimeSessionToken = NormalizeBuildGitHash(query?["runtimeSessionToken"].ToString());
        if (!_tracker.Register(
                runnerId,
                Context.ConnectionId,
                runtimeGeneration,
                buildGitHash,
                artifactDigest,
                runtimeSessionToken))
        {
            _log.LogWarning("Rejected stale or incomplete runner connection for {RunnerId}", runnerId);
            return;
        }

        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UpdateBuildGitHashAsync(buildGitHash);
    }

    public Task<string> Ping() => Task.FromResult(Context.ConnectionId ?? string.Empty);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var runnerId = Context.GetHttpContext()?.Request.Query["runnerId"].ToString();
        if (!string.IsNullOrEmpty(runnerId))
        {
            var query = Context.GetHttpContext()?.Request.Query;
            var generation = NormalizeBuildGitHash(query?["runtimeGeneration"].ToString());
            var runtimeSessionToken = NormalizeBuildGitHash(query?["runtimeSessionToken"].ToString());
            var sessions = generation is not null && runtimeSessionToken is not null
                ? _tracker.UnregisterAndGetSessions(runnerId, generation, Context.ConnectionId, runtimeSessionToken)
                : _tracker.UnregisterAndGetSessions(runnerId, Context.ConnectionId);
            foreach (var sessionId in sessions)
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
