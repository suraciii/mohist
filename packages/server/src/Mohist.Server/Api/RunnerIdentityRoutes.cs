using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;

namespace Mohist.Server.Api;

public static class RunnerIdentityRoutes
{
    public static WebApplication MapRunnerIdentityRoutes(this WebApplication app)
    {
        app.MapGet("/api/runner/identity", async (HttpContext context, IGrainFactory grains, RunnerConnectionTracker tracker) =>
        {
            var hostname = context.Request.Query["hostname"].ToString();
            if (string.IsNullOrWhiteSpace(hostname))
                hostname = Environment.MachineName;

            var globalRegistry = grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
            var globalRunners = await globalRegistry.ListAllAsync();
            var candidate = globalRunners.FirstOrDefault(r =>
                string.Equals(r.Hostname, hostname, StringComparison.OrdinalIgnoreCase));
            if (candidate is null)
                return ApiResults.NotFound($"No runner registered for hostname '{hostname}'");

            var grain = grains.GetGrain<IRunnerGrain>(candidate.RunnerId);
            RunnerRuntimeState? runtime = null;
            try
            {
                runtime = await grain.GetRuntimeStateAsync();
            }
            catch
            {
                // grain unavailable; report best-effort
            }

            var connectionId = tracker.GetConnectionId(candidate.RunnerId);
            var isOnline = runtime is { Status: RunnerStatus.Online };
            return ApiResults.Ok(new RunnerIdentityView(
                candidate.RunnerId,
                candidate.Hostname,
                candidate.BuildGitHash,
                candidate.Component,
                candidate.Version,
                candidate.SourceRevision,
                candidate.TreeHash,
                candidate.ArtifactDigest,
                candidate.ReleaseId,
                candidate.Generation,
                isOnline ? "online" : "offline",
                runtime?.LastHeartbeatAt,
                connectionId is not null ? "connected" : "disconnected",
                tracker.GetConnectionGeneration(candidate.RunnerId)));
        }).RequireScopes(Scope.Operator);

        return app;
    }
}

public sealed record RunnerIdentityView(
    string RunnerId,
    string Hostname,
    string? BuildGitHash,
    string? Component,
    string? Version,
    string? SourceRevision,
    string? TreeHash,
    string? ArtifactDigest,
    string? ReleaseId,
    long? Generation,
    string Status,
    DateTimeOffset? LastHeartbeatAt,
    string ConnectionState,
    string? ConnectionGeneration = null);
