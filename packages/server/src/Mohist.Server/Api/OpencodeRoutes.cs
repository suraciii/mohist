using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
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

        group.MapGet("/models", async (string? runtime, IGrainFactory grains) =>
        {
            var selectedRuntime = string.IsNullOrWhiteSpace(runtime)
                ? AgentConfigSchema.OpenCodeRuntime
                : runtime.Trim().ToLowerInvariant();
            if (!AgentConfigSchema.AllowedRuntimes.Contains(selectedRuntime))
                return ApiResults.BadRequest("runtime must be 'opencode' or 'pi'", "runtime_invalid");

            var registry = grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
            var models = (await registry.ListCoderModelsByRuntimeAsync(selectedRuntime)).ToArray();

            var visibleModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var modelVariants = await registry.ListCoderModelVariantsByRuntimeAsync(selectedRuntime);
            var reasoningEfforts = await registry.ListCoderReasoningEffortsByRuntimeAsync(selectedRuntime);

            return ApiResults.Ok(new { models = visibleModels, modelVariants, reasoningEfforts });
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
