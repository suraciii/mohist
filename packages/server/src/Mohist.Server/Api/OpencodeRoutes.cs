using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Api;

public static class OpencodeRoutes
{
    public const string AgentCommandEnvironmentVariable = "MOHIST_AGENT_COMMAND";

    public static WebApplication MapOpencodeRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/opencode")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/models", async (HttpContext context, IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();

            var globalRegistry = grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
            var models = await globalRegistry.ListCoderModelsAsync();

            var projectRegistry = grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(project.Id));
            models = models.Concat(await projectRegistry.ListCoderModelsAsync()).ToArray();

            var visibleModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return ApiResults.Ok(new { models = visibleModels });
        });

        app.MapGet("/api/opencode/runtime", async (ConfigService svc, IConfiguration configuration, IEnvironmentVariableProvider environment) =>
        {
            var agent = await svc.GetAgentConfigAsync();
            var model = agent?.GetValueOrDefault("model")?.ToString();
            return ApiResults.Ok(new
            {
                mode = "local-opencode",
                command = configuration["Mohist:AgentCommand"]
                    ?? environment.GetEnvironmentVariable(AgentCommandEnvironmentVariable)
                    ?? "opencode",
                model = string.IsNullOrWhiteSpace(model) ? null : model,
                note = "Mohist delegates coder work to the external opencode runtime.",
            });
        });

        return app;
    }
}
