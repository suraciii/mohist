using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Project-scoped composite launch observation read. Returns
/// <see cref="AgentLaunchObservationDto"/> for the
/// launch owned by the supplied <c>jobId</c>. Enforces project isolation
/// from the AgentJob grain's stored <c>ProjectId</c> (manual-launch keys
/// are global GUIDs). Read-only — the observation surface MUST NOT
/// create another SessionInput, AgentTurn, or AgentJob; reads are pure
/// observation. Replays of the same observation are safe and idempotent.
/// </summary>
public static class AgentLaunchObservationRoutes
{
    public static WebApplication MapAgentLaunchObservationRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/agent-jobs/{jobId}/launch-observation", (
            HttpContext context,
            string projectRef,
            string jobId,
            AgentLaunchObservationAssembler assembler,
            CancellationToken ct) =>
            HandleObservationAsync(context.GetResolvedProject(), jobId, assembler, ct));

        return app;
    }

    internal static async Task<IResult> HandleObservationAsync(
        ProjectInfo project,
        string jobId,
        AgentLaunchObservationAssembler assembler,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return ApiResults.NotFound("Job not found");
        }

        var observation = await assembler.ReadAsync(project.Id, jobId, ct);
        if (observation is null)
        {
            return ApiResults.NotFound("Job not found");
        }

        return ApiResults.Ok(observation);
    }
}
