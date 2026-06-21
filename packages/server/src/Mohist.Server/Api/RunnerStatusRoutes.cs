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

        group.MapGet("/{runnerId}", async (HttpContext context, string runnerId, RunnerStatusService projection) =>
        {
            var project = context.GetResolvedProject();
            var runner = await projection.GetRunnerAsync(project.Id, runnerId);
            if (runner is null)
            {
                return ApiResults.Fail($"Runner '{runnerId}' not found", 404, "runner_not_found");
            }

            return ApiResults.Ok(new RunnerStatusDetailResponse(runner));
        });

        return app;
    }
}

public sealed record RunnerStatusListResponse(IReadOnlyList<RunnerStatusView> Runners);

public sealed record RunnerStatusDetailResponse(RunnerStatusView Runner);
