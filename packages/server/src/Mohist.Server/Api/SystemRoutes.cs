using Mohist.Server.SystemInfo;

namespace Mohist.Server.Api;

public static class SystemRoutes
{
    public static WebApplication MapSystemRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/info", async (SystemInfoService systemInfo, CancellationToken ct) =>
            ApiResults.Ok(await systemInfo.GetSystemInfoAsync()));

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

        app.MapPost("/api/system/update/outcome", async (SystemUpdateOutcomeRequest? request, SystemUpdateService updates, CancellationToken ct) =>
        {
            var payload = request ?? new SystemUpdateOutcomeRequest();
            if (string.IsNullOrWhiteSpace(payload.Status) || string.IsNullOrWhiteSpace(payload.Outcome))
            {
                return ApiResults.BadRequest("status and outcome are required", "invalid_outcome");
            }

            try
            {
                var response = await updates.RecordCliOutcomeAsync(payload, ct);
                return ApiResults.Ok(new SystemUpdateOutcomeResponse(response));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_outcome");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "job_id_mismatch");
            }
        });

        app.MapGet("/api/system/consistency", async (SystemUpdateService updates, CancellationToken ct) =>
        {
            return ApiResults.Ok(await updates.GetConsistencyAsync(ct));
        });

        return app;
    }
}
