using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Api;

public static class SystemRoutes
{
    public static WebApplication MapSystemRoutes(this WebApplication app)
    {
        app.MapGet("/api/system/info", async (SystemInfoService systemInfo, CancellationToken ct) =>
            ApiResults.Ok(await systemInfo.GetSystemInfoAsync())).RequireScopes(Scope.Operator);

        app.MapPost(
            "/api/system/update",
            () => ApiResults.Fail(
                "Server-initiated updates are disabled; run mo update with --repo-root",
                409,
                "cli_only_update"))
            .RequireScopes(Scope.Operator);

        app.MapGet("/api/system/update/status", async (SystemUpdateService updates, CancellationToken ct) =>
        {
            return ApiResults.Ok(await updates.GetStatusEnvelopeAsync(ct));
        }).RequireScopes(Scope.Operator);

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
        }).RequireScopes(Scope.Operator);

        app.MapGet("/api/system/consistency", async (SystemUpdateService updates, CancellationToken ct) =>
        {
            return ApiResults.Ok(await updates.GetConsistencyAsync(ct));
        }).RequireScopes(Scope.Operator);

        return app;
    }
}
