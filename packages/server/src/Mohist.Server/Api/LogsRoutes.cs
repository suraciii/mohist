using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Logging;

namespace Mohist.Server.Api;

public static class LogsRoutes
{
    public static WebApplication MapLogsRoutes(this WebApplication app)
    {
        app.MapGet("/api/logs/tail", async (
            long? cursor,
            int? limit,
            int? maxBytes,
            LogTailReader reader) =>
        {
            if (cursor is < 0)
                return ApiResults.BadRequest("cursor must be greater than or equal to 0", "invalid_cursor");
            if (limit is <= 0)
                return ApiResults.BadRequest("limit must be greater than 0", "invalid_limit");
            if (maxBytes is <= 0)
                return ApiResults.BadRequest("maxBytes must be greater than 0", "invalid_max_bytes");
            if (maxBytes is > LogTailReader.MaximumTailMaxBytes)
            {
                return ApiResults.BadRequest(
                    $"maxBytes must be less than or equal to {LogTailReader.MaximumTailMaxBytes}",
                    "invalid_max_bytes");
            }

            var result = await reader.ReadTailAsync(cursor, limit, maxBytes);
            return ApiResults.Ok(result);
        }).RequireScopes(Scope.Operator);

        return app;
    }
}
