using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Config;

namespace Mohist.Server.Api;

public static class ConfigRoutes
{
    public static WebApplication MapConfigRoutes(this WebApplication app)
    {
        app.MapGet("/api/model", async (ConfigService svc) =>
        {
            var agent = await svc.GetAgentConfigAsync();
            var model = agent?.GetValueOrDefault("model")?.ToString();
            return ApiResults.Ok(new { model = string.IsNullOrWhiteSpace(model) ? null : model });
        });

        app.MapPut("/api/model", async (ModelRequest req, ConfigService svc) =>
        {
            await svc.SetAgentModelAsync(req.Model);
            return ApiResults.Ok(new { req.Model });
        });

        app.MapGet("/api/opencode-model", async (ConfigService svc) =>
        {
            var agent = await svc.GetAgentConfigAsync();
            var model = agent?.GetValueOrDefault("model")?.ToString();
            return ApiResults.Ok(new { model = string.IsNullOrWhiteSpace(model) ? null : model });
        });

        app.MapPut("/api/opencode-model", async (ModelRequest req, ConfigService svc) =>
        {
            await svc.SetAgentModelAsync(req.Model);
            return ApiResults.Ok(new { req.Model });
        });

        app.MapGet("/api/agent-config", async (ConfigService svc) =>
        {
            var agent = await svc.GetAgentConfigAsync();
            var stageAgents = await svc.GetStageAgentConfigsAsync();
            return ApiResults.Ok(new { agent, stageAgents = stageAgents.Count == 0 ? null : stageAgents });
        });

        app.MapPut("/api/agent-config", async (AgentConfigRequest req, ConfigService svc) =>
        {
            if (req.Agent is null) await svc.ClearAsync("agent");
            else await svc.SetAsync("agent", req.Agent);

            if (req.StageAgents is null) await svc.ClearAsync("stageAgents");
            else await svc.SetAsync("stageAgents", req.StageAgents);

            return ApiResults.Ok(new { agent = await svc.GetAgentConfigAsync(), stageAgents = await svc.GetStageAgentConfigsAsync() });
        });

        var config = app.MapGroup("/api/config");

        config.MapGet("/", async (ConfigService svc) =>
        {
            var cfg = await svc.GetConfigAsync();
            foreach (var key in cfg.Keys.ToList())
            {
                if (ConfigRouteHelpers.IsSecretKey(key))
                    cfg[key] = "***";
            }
            return ApiResults.Ok(cfg);
        }).RequireScopes(Scope.Operator);

        config.MapGet("/list", async (ConfigService svc) =>
        {
            var all = await svc.GetAllAsync();
            var safe = new Dictionary<string, string>();
            foreach (var (key, value) in all)
            {
                safe[key] = ConfigRouteHelpers.IsSecretKey(key) ? "***" : value;
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
        }).RequireScopes(Scope.Operator);

        return app;
    }
}

public record ConfigValueRequest(object? Value);
public record ModelRequest(string? Model);
public record AgentConfigRequest(Dictionary<string, object?>? Agent, Dictionary<string, Dictionary<string, object?>>? StageAgents = null);

internal static class ConfigRouteHelpers
{
    public static bool IsSecretKey(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase);
}
