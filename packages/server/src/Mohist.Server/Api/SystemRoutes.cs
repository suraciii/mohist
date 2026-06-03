using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Infrastructure;

namespace Mohist.Server.Api;

public static class SystemRoutes
{
    public static WebApplication MapSystemRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/info", async (SystemInfoService systemInfo, CancellationToken ct) =>
            ApiResults.Ok(await systemInfo.GetSystemInfoAsync()));

        app.MapGet("/api/system/templates", async (ProjectWorkflowProfileManager profileManager) =>
            ApiResults.Ok(await profileManager.ListSystemTemplatesAsync()));

        app.MapPost("/api/system/update", async (SystemUpdateRequest? request, SystemUpdateService updates, CancellationToken ct) =>
        {
            var result = await updates.StartAsync(request ?? new SystemUpdateRequest(), ct);
            if (!result.Started)
            {
                return result.Code == "update_in_progress"
                    ? ApiResults.Conflict(result.Error ?? "A system update is already in progress", result.Code)
                    : ApiResults.Fail(result.Error ?? "System update failed", 400, result.Code);
            }

            return Results.Json(
                new ApiResponse<SystemUpdateStartResponse>(
                    true,
                    new SystemUpdateStartResponse(result.Status!)),
                statusCode: 202);
        });

        app.MapGet("/api/system/update/status", async (SystemUpdateService updates, CancellationToken ct) =>
        {
            return ApiResults.Ok(await updates.GetStatusEnvelopeAsync(ct));
        });

        return app;
    }
}
