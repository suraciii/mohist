using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config.Domain;
using Mohist.Server.Storage.Db;

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
        app.MapGet("/api/providers", async (IDbContextFactory<MohistDbContext> dbFactory) =>
        {
            var configured = await LoadConfiguredProvidersAsync(dbFactory);
            var result = BuiltinProviders.Select(p => new
            {
                p.Id,
                p.Name,
                BaseURL = configured.GetValueOrDefault($"provider:{p.Id}:baseURL"),
                Configured = configured.ContainsKey($"provider:{p.Id}:apiKey"),
                Source = configured.ContainsKey($"provider:{p.Id}:apiKey") ? "config" : "none",
                p.IsBuiltin,
                p.IsDefault,
                apiKeyMasked = configured.TryGetValue($"provider:{p.Id}:apiKey", out var key) ? Mask(key) : null,
            }).ToList();

            var customIds = configured.Keys
                .Where(k => k.StartsWith("provider:", StringComparison.Ordinal))
                .Select(k => k.Split(':')[1])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => result.All(p => p.Id != id));

            result.AddRange(customIds.Select(id => new
            {
                Id = id,
                Name = configured.GetValueOrDefault($"provider:{id}:name") ?? id,
                BaseURL = configured.GetValueOrDefault($"provider:{id}:baseURL"),
                Configured = configured.ContainsKey($"provider:{id}:apiKey"),
                Source = configured.ContainsKey($"provider:{id}:apiKey") ? "config" : "none",
                IsBuiltin = false,
                IsDefault = false,
                apiKeyMasked = configured.TryGetValue($"provider:{id}:apiKey", out var key) ? Mask(key) : null,
            }));

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

        app.MapPost("/api/providers/test", (ProviderFormRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ApiKey))
                return ApiResults.BadRequest("apiKey is required");
            return ApiResults.Ok(new { success = true });
        });

        app.MapPost("/api/providers/{id}", async (string id, ProviderFormRequest req, IDbContextFactory<MohistDbContext> dbFactory) =>
        {
            if (string.IsNullOrWhiteSpace(req.ApiKey))
                return ApiResults.BadRequest("apiKey is required");
            await SetProviderValueAsync(dbFactory, id, "apiKey", req.ApiKey);
            if (!string.IsNullOrWhiteSpace(req.Name)) await SetProviderValueAsync(dbFactory, id, "name", req.Name);
            if (!string.IsNullOrWhiteSpace(req.BaseURL)) await SetProviderValueAsync(dbFactory, id, "baseURL", req.BaseURL);
            if (req.Models is { Length: > 0 }) await SetProviderValueAsync(dbFactory, id, "models", string.Join(",", req.Models));
            if (!string.IsNullOrWhiteSpace(req.Sdk)) await SetProviderValueAsync(dbFactory, id, "sdk", req.Sdk);
            return ApiResults.Ok(new { id, configured = true });
        });

        app.MapDelete("/api/providers/{id}", async (string id, IDbContextFactory<MohistDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var prefix = $"provider:{id}:";
            var rows = await db.Configs.Where(c => c.Key.StartsWith(prefix)).ToListAsync();
            db.Configs.RemoveRange(rows);
            await db.SaveChangesAsync();
            return ApiResults.Ok(new { id });
        });

        return app;
    }

    private static async Task<Dictionary<string, string>> LoadConfiguredProvidersAsync(IDbContextFactory<MohistDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Configs.AsNoTracking()
            .Where(c => c.Key.StartsWith("provider:"))
            .ToDictionaryAsync(c => c.Key, c => c.Value);
    }

    private static async Task SetProviderValueAsync(IDbContextFactory<MohistDbContext> dbFactory, string id, string name, string value)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = $"provider:{id}:{name}";
        var row = await db.Configs.FindAsync(key);
        if (row is null)
            db.Configs.Add(new ConfigEntry { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static string Mask(string key) => key.Length <= 8 ? "********" : key[..4] + new string('*', key.Length - 8) + key[^4..];
}

public record ProviderInfo(string Id, string Name, bool Configured, bool IsBuiltin, bool IsDefault = false);
public record ModelInfo(string Id, string Name, string[] Badges, int ContextWindow);
public record ProviderFormRequest(string? Name, string ApiKey, string? BaseURL, string[]? Models, string? Sdk);
