using Mohist.Server.SystemInfo;

namespace Mohist.Server.Api;

public static class HealthRoutes
{
    public static WebApplication MapHealthRoutes(this WebApplication app)
    {
        app.MapGet("/api/health", (IRuntimeBuildInfo buildInfo) =>
        {
            return ApiResults.Ok(new
            {
                status = "ok",
                timestamp = DateTime.UtcNow.ToString("o"),
                version = buildInfo.Version,
                gitHash = buildInfo.GitHash,
            });
        });

        return app;
    }
}
