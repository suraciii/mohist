using Mohist.Server.Config;

namespace Mohist.Server.Api;

public static class ConfigRoutes
{
    public static WebApplication MapConfigRoutes(this WebApplication app)
    {
        app.MapGet("/api/model", async (ConfigService svc) =>
        {
            var all = await svc.GetAllAsync();
            all.TryGetValue("model", out var model);
            return ApiResults.Ok(new { model = string.IsNullOrWhiteSpace(model) ? null : model });
        });

        app.MapPut("/api/model", async (ModelRequest req, ConfigService svc) =>
        {
            if (req.Model is null) await svc.ClearAsync("model");
            else await svc.SetAsync("model", req.Model);
            return ApiResults.Ok(new { req.Model });
        });

        app.MapGet("/api/opencode-model", async (ConfigService svc) =>
        {
            var all = await svc.GetAllAsync();
            all.TryGetValue("model", out var model);
            return ApiResults.Ok(new { model = string.IsNullOrWhiteSpace(model) ? null : model });
        });

        app.MapPut("/api/opencode-model", async (ModelRequest req, ConfigService svc) =>
        {
            if (req.Model is null) await svc.ClearAsync("model");
            else await svc.SetAsync("model", req.Model);
            return ApiResults.Ok(new { req.Model });
        });

        app.MapGet("/api/stage-models", async (ConfigService svc) =>
        {
            var all = await svc.GetAllAsync();
            if (!all.TryGetValue("stageModels", out var json) || string.IsNullOrWhiteSpace(json))
                return ApiResults.Ok(new { stageModels = (Dictionary<string, string>?)null });
            return ApiResults.Ok(new { stageModels = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) });
        });

        app.MapPut("/api/stage-models", async (StageModelsRequest req, ConfigService svc) =>
        {
            if (req.StageModels is null) await svc.ClearAsync("stageModels");
            else await svc.SetAsync("stageModels", req.StageModels);
            return ApiResults.Ok(new { req.StageModels });
        });

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
public record ModelRequest(string? Model);
public record StageModelsRequest(Dictionary<string, string>? StageModels);
