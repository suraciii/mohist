using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

public static class RunnerStatusRoutes
{
    public static WebApplication MapRunnerStatusRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runners");

        group.MapGet("/", async (string? projectId, RunnerStatusService projection) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return ApiResults.BadRequest("projectId is required");
            }

            var runners = await projection.GetRunnersAsync(projectId);
            return ApiResults.Ok(new RunnerStatusListResponse(runners));
        });

        return app;
    }
}

public sealed record RunnerStatusListResponse(IReadOnlyList<RunnerStatusView> Runners);