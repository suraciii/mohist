namespace Mohist.Server.Api;

public static class ProvidersRoutes
{
    private static readonly List<ProviderInfo> BuiltinProviders =
    [
        new("anthropic", "Anthropic", true, true),
        new("openai", "OpenAI", true, true),
        new("deepseek", "DeepSeek", true, true),
        new("kimi", "Kimi", true, true),
        new("qwen", "Qwen", true, true),
        new("glm", "GLM", true, true),
    ];

    private static readonly Dictionary<string, List<ModelInfo>> ProviderModels = new()
    {
        ["anthropic"] =
        [
            new("claude-sonnet-4-20250514", "Claude Sonnet 4", ["recommended"], 200000),
            new("claude-opus-4-20250514", "Claude Opus 4", ["flagship"], 200000),
        ],
        ["openai"] =
        [
            new("gpt-4o", "GPT-4o", ["recommended"], 128000),
            new("gpt-4o-mini", "GPT-4o Mini", ["fast"], 128000),
        ],
        ["deepseek"] =
        [
            new("deepseek-chat", "DeepSeek Chat", ["recommended"], 64000),
        ],
    };

    public static WebApplication MapProvidersRoutes(this WebApplication app)
    {
        app.MapGet("/api/providers", () =>
        {
            var result = BuiltinProviders.Select(p => new
            {
                p.Id,
                p.Name,
                p.Configured,
                p.IsBuiltin,
                p.IsDefault,
                apiKeyMasked = p.Configured ? "sk-****" : null,
            });
            return ApiResults.Ok(result);
        });

        app.MapGet("/api/providers/models", () =>
        {
            var result = ProviderModels.Select(kv => new
            {
                id = kv.Key,
                name = kv.Key,
                configured = true,
                models = kv.Value.Select(m => new
                {
                    m.Id,
                    m.Name,
                    m.Badges,
                    m.ContextWindow,
                }),
            });
            return ApiResults.Ok(result);
        });

        return app;
    }
}

public record ProviderInfo(string Id, string Name, bool Configured, bool IsBuiltin, bool IsDefault = false);
public record ModelInfo(string Id, string Name, string[] Badges, int ContextWindow);
