using Mohist.Server.Runner.Services;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

public static class RunnerStatusRoutes
{
    public static WebApplication MapRunnerStatusRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/runners");

        group.MapGet("/", async (string projectRef, RunnerStatusService projection, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var runners = await projection.GetRunnersAsync(project.Id);
            return ApiResults.Ok(new RunnerStatusListResponse(runners));
        });

        return app;
    }
}

public sealed record RunnerStatusListResponse(IReadOnlyList<RunnerStatusView> Runners);
