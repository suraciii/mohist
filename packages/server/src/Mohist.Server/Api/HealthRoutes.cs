namespace Mohist.Server.Api;

public static class HealthRoutes
{
    public static WebApplication MapHealthRoutes(this WebApplication app)
    {
        app.MapGet("/api/health", () =>
        {
            return Results.Ok(new
            {
                status = "ok",
                timestamp = DateTime.UtcNow.ToString("o"),
                version = (string?)null,
                gitHash = (string?)null
            });
        });

        return app;
    }
}
