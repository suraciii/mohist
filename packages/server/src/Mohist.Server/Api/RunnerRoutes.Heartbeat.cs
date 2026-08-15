using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    internal static async Task<IResult> HandleHeartbeatAsync(
        string runnerId,
        HttpRequest request,
        IGrainFactory grains,
        RunnerConnectionTracker connections)
    {
        var runner = grains.GetGrain<IRunnerGrain>(runnerId);
        var req = request.ContentLength.GetValueOrDefault() > 0
            ? await JsonSerializer.DeserializeAsync<RunnerHeartbeatRequest>(request.Body, JSON.Options)
            : null;

        if (req is not null)
        {
            var info = new RunnerInfo(
                runnerId,
                req.Capabilities ?? [],
                req.Hostname ?? Environment.MachineName,
                req.ProjectId,
                req.CoderModels,
                BuildGitHash: NormalizeBuildGitHash(req.BuildGitHash),
                CoderModelVariants: NormalizeCoderModelVariants(req.CoderModelVariants),
                ActionCatalog: req.ActionCatalog,
                RuntimeCatalogs: NormalizeRuntimeCatalogs(req.RuntimeCatalogs),
                Component: NormalizeIdentity(req.Component),
                Version: NormalizeIdentity(req.Version),
                SourceRevision: NormalizeIdentity(req.SourceRevision) ?? NormalizeBuildGitHash(req.BuildGitHash),
                TreeHash: NormalizeIdentity(req.TreeHash),
                ArtifactDigest: NormalizeIdentity(req.ArtifactDigest),
                ReleaseId: NormalizeIdentity(req.ReleaseId),
                Generation: req.Generation > 0 ? req.Generation : null);
            await runner.HeartbeatRepairAsync(info);

            if (!string.IsNullOrWhiteSpace(req.ConnectionId))
                connections.Register(runnerId, req.ConnectionId);
        }
        else
        {
            await runner.HeartbeatAsync();
        }

        return Results.Ok();
    }
}
