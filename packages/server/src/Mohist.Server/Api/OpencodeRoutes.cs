using Mohist.Server.Config;
using Mohist.Server.Grains;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Api;

public static class OpencodeRoutes
{
    public static WebApplication MapOpencodeRoutes(this WebApplication app)
    {
        app.MapGet("/api/opencode/models", async (string projectId, IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(projectId));
            return ApiResults.Ok(new { models = await registry.ListCoderModelsAsync() });
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
