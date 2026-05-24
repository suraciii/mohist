using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Config.Domain;

public class ConfigService
{
    private readonly IDbContextFactory<MohistDbContext> _contextFactory;
    private readonly ILogger<ConfigService> _log;
    private readonly Dictionary<string, (string type, object? defaultValue)> _schema = new()
    {
        ["serverPort"] = ("number", 3456),
        ["serverHost"] = ("string", "localhost"),
        ["pollInterval"] = ("number", 5000),
        ["maxConcurrentAgents"] = ("number", 3),
        ["agentTimeout"] = ("number", 600),
        ["taskTimeout"] = ("number", 600),
        ["stageTimeout"] = ("number", 3600),
        ["maxGracePeriods"] = ("number", 3),
        ["model"] = ("string", null),
        ["stageModels"] = ("json", null),
    };

    public ConfigService(IDbContextFactory<MohistDbContext> contextFactory, ILogger<ConfigService> log)
    {
        _contextFactory = contextFactory;
        _log = log;
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var rows = await db.Configs.ToListAsync();
        return rows.ToDictionary(r => r.Key, r => r.Value);
    }

    public async Task<Dictionary<string, object?>> GetConfigAsync()
    {
        var all = await GetAllAsync();
        var result = new Dictionary<string, object?>();
        foreach (var (key, (type, defaultValue)) in _schema)
        {
            if (all.TryGetValue(key, out var value))
                result[key] = ParseValue(type, value);
            else if (defaultValue != null)
                result[key] = defaultValue;
        }
        return result;
    }

    public async Task SetAsync(string key, object value)
    {
        if (!_schema.ContainsKey(key))
            throw new InvalidOperationException($"Unknown config key: {key}");

        await using var db = await _contextFactory.CreateDbContextAsync();
        var strValue = value switch
        {
            null => "",
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(value),
        };

        var row = await db.Configs.FindAsync(key);
        if (row is null)
        {
            db.Configs.Add(new ConfigEntry { Key = key, Value = strValue, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            row.Value = strValue;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        _log.LogInformation("Config {Key} updated", key);
    }

    public async Task ClearAsync(string key)
    {
        if (!_schema.ContainsKey(key))
            throw new InvalidOperationException($"Unknown config key: {key}");

        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.Configs.FindAsync(key);
        if (row is not null)
        {
            db.Configs.Remove(row);
            await db.SaveChangesAsync();
        }
    }

    public (bool valid, string? error) Validate(string key, string value)
    {
        if (!_schema.TryGetValue(key, out var def))
            return (false, $"Unknown config key: {key}");

        var (type, _) = def;
        return type switch
        {
            "number" => int.TryParse(value, out _) ? (true, null) : (false, $"{key} must be a number"),
            "json" => JsonIsValid(value) ? (true, null) : (false, $"{key} must be valid JSON"),
            _ => (true, null),
        };
    }

    private static object? ParseValue(string type, string value) => type switch
    {
        "number" => int.TryParse(value, out var n) ? n : value,
        "json" => JsonSerializer.Deserialize<JsonElement>(value),
        _ => value,
    };

    private static bool JsonIsValid(string value)
    {
        try { JsonSerializer.Deserialize<JsonElement>(value); return true; }
        catch { return false; }
    }
}

public class ConfigEntry
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
