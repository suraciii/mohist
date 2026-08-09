using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;

namespace Mohist.Server.Api;

public static class RunnerIdentityRoutes
{
    public static WebApplication MapRunnerIdentityRoutes(this WebApplication app)
    {
        app.MapGet("/api/runner/identity", async (HttpContext context, RunnerConnectionTracker tracker, CancellationToken cancellationToken) =>
        {
            var runnerId = context.Request.Query["runnerId"].ToString().Trim();
            var generation = context.Request.Query["generation"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(runnerId) || string.IsNullOrWhiteSpace(generation))
                return ApiResults.BadRequest("runnerId and generation are required");

            var wait = bool.TryParse(context.Request.Query["wait"], out var requestedWait) && requestedWait;
            var identity = tracker.GetRuntimeIdentity(runnerId, generation);
            if (wait && identity is not { IsOnline: true })
                identity = await tracker.WaitForRuntimeIdentityAsync(runnerId, generation, cancellationToken);
            if (identity is null)
                return ApiResults.NotFound($"No connected runner instance '{runnerId}' generation '{generation}'");

            return ApiResults.Ok(RunnerIdentityView.FromRuntimeIdentity(identity));
        }).RequireScopes(Scope.Operator);

        return app;
    }
}

public sealed record RunnerIdentityView(
    string RunnerId,
    string RuntimeGeneration,
    string? BuildGitHash,
    string? ArtifactDigest,
    string Status,
    DateTimeOffset? LastHeartbeatAt,
    string ConnectionState)
{
    internal static RunnerIdentityView FromRuntimeIdentity(RunnerRuntimeConnection identity)
    {
        var exactRuntimeMatch = identity.IsOnline;
        return new RunnerIdentityView(
            identity.RunnerId,
            identity.RuntimeGeneration,
            identity.BuildGitHash,
            identity.ArtifactDigest,
            exactRuntimeMatch ? "online" : "offline",
            null,
            exactRuntimeMatch ? "connected" : "disconnected");
    }
}
