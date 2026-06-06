using Microsoft.AspNetCore.Http;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

public static class RunnerStatusRoutes
{
    public static WebApplication MapRunnerStatusRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/runners")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (HttpContext context, RunnerStatusService projection) =>
        {
            var project = context.GetResolvedProject();
            var runners = await projection.GetRunnersAsync(project.Id);
            return ApiResults.Ok(new RunnerStatusListResponse(runners));
        });

        return app;
    }
}

public sealed record RunnerStatusListResponse(IReadOnlyList<RunnerStatusView> Runners);
