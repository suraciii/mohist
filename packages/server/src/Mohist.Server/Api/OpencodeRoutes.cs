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
            var models = (await globalRegistry.ListCoderModelsAsync())
                .Concat(await grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(project.Id)).ListCoderModelsAsync())
                .ToArray();

            var visibleModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var globalVariants = await globalRegistry.ListCoderModelVariantsAsync();
            var projectVariants = await grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(project.Id)).ListCoderModelVariantsAsync();

            var modelVariants = MergeVariantMaps(globalVariants, projectVariants);

            return ApiResults.Ok(new { models = visibleModels, modelVariants });
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

    private static IReadOnlyDictionary<string, string[]> MergeVariantMaps(
        IReadOnlyDictionary<string, string[]> global,
        IReadOnlyDictionary<string, string[]> project)
    {
        if ((global is null || global.Count == 0) && (project is null || project.Count == 0))
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { global, project })
        {
            if (source is null) continue;
            foreach (var entry in source)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                var variants = (entry.Value ?? [])
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (variants.Length == 0)
                    continue;

                if (merged.TryGetValue(entry.Key, out var existing))
                {
                    merged[entry.Key] = existing
                        .Concat(variants)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }
                else
                {
                    merged[entry.Key] = variants;
                }
            }
        }

        return merged
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
