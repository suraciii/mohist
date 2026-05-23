using Mohist.Server.Config.Domain;

namespace Mohist.Server.Api;

public static class ConfigRoutes
{
    public static WebApplication MapConfigRoutes(this WebApplication app)
    {
        var config = app.MapGroup("/api/config");

        config.MapGet("/", async (ConfigService svc) =>
        {
            var cfg = await svc.GetConfigAsync();
            return ApiResults.Ok(cfg);
        });

        config.MapGet("/list", async (ConfigService svc) =>
        {
            var all = await svc.GetAllAsync();
            var safe = new Dictionary<string, string>();
            foreach (var (key, value) in all)
            {
                safe[key] = key.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    ? "***"
                    : value;
            }
            return ApiResults.Ok(safe);
        });

        config.MapPut("/{key}", async (string key, ConfigValueRequest req, ConfigService svc) =>
        {
            if (req.Value is null)
                return ApiResults.BadRequest("value is required");

            var (valid, error) = svc.Validate(key, req.Value.ToString()!);
            if (!valid)
                return ApiResults.BadRequest(error!);

            await svc.SetAsync(key, req.Value);
            var cfg = await svc.GetConfigAsync();
            return ApiResults.Ok(cfg);
        });

        return app;
    }
}

public record ConfigValueRequest(object? Value);
