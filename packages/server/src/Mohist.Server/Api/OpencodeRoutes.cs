using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static class OpencodeRoutes
{
    public static WebApplication MapOpencodeRoutes(this WebApplication app)
    {
        app.MapGet("/api/opencode/models", async (string? projectId, IGrainFactory grains) =>
        {
            var globalRegistry = grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
            var models = await globalRegistry.ListCoderModelsAsync();

            if (!string.IsNullOrWhiteSpace(projectId))
            {
                var projectRegistry = grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(projectId));
                models = models.Concat(await projectRegistry.ListCoderModelsAsync()).ToArray();
            }

            var visibleModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return ApiResults.Ok(new { models = visibleModels });
        });

        app.MapGet("/api/opencode/runtime", async (ConfigService svc, IConfiguration configuration) =>
        {
            var agent = await svc.GetAgentConfigAsync();
            var model = agent?.GetValueOrDefault("model")?.ToString();
            return ApiResults.Ok(new
            {
                mode = "local-opencode",
                command = configuration["Mohist:AgentCommand"]
                    ?? Environment.GetEnvironmentVariable("MOHIST_AGENT_COMMAND")
                    ?? "opencode",
                model = string.IsNullOrWhiteSpace(model) ? null : model,
                note = "Mohist delegates coder work to the external opencode runtime.",
            });
        });

        return app;
    }
}
