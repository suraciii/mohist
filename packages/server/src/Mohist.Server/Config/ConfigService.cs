using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Server.Config;

public class ConfigService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigService> _log;
    private readonly string _configPath;

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
        ["agent"] = ("json", null),
        ["stageAgents"] = ("json", null),
        ["logLevel"] = ("string", "INFO"),
    };

    public ConfigService(IConfiguration configuration, ILogger<ConfigService> log, string? configPath = null)
    {
        _configuration = configuration;
        _log = log;
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "config.jsonc");
    }

    public Task<Dictionary<string, string>> GetAllAsync()
    {
        var result = new Dictionary<string, string>();
        var fileValues = ReadConfigFile();

        foreach (var (key, _) in _schema)
        {
            var value = GetConfigValue(key, fileValues);
            if (value != null)
                result[key] = value.ToString()!;
        }

        return Task.FromResult(result);
    }

    public Task<Dictionary<string, object?>> GetConfigAsync()
    {
        var result = new Dictionary<string, object?>();
        var fileValues = ReadConfigFile();

        foreach (var (key, (type, defaultValue)) in _schema)
        {
            var value = GetConfigValue(key, fileValues);
            if (value != null)
                result[key] = ParseValue(type, value.ToString()!);
            else if (defaultValue != null)
                result[key] = defaultValue;
        }

        return Task.FromResult(result);
    }

    public async Task SetAsync(string key, object value)
    {
        if (!_schema.ContainsKey(key))
            throw new InvalidOperationException($"Unknown config key: {key}");

        var strValue = value switch
        {
            null => "",
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(value),
        };

        await WriteConfigFileAsync(key, strValue);
        _log.LogInformation("Config {Key} updated in {Path}", key, _configPath);
    }

    public async Task ClearAsync(string key)
    {
        if (!_schema.ContainsKey(key))
            throw new InvalidOperationException($"Unknown config key: {key}");

        await WriteConfigFileAsync(key, null);
        _log.LogInformation("Config {Key} cleared from {Path}", key, _configPath);
    }

    /// <summary>
    /// 获取全局默认 agent 配置（向后兼容：如果 agent 不存在但 model 存在，从 model 构建）。
    /// </summary>
    public Task<Dictionary<string, object?>?> GetAgentConfigAsync()
    {
        var fileValues = ReadConfigFile();

        // 优先读取 agent 对象配置
        var agentJson = GetConfigValue("agent", fileValues);
        if (!string.IsNullOrWhiteSpace(agentJson))
        {
            try
            {
                var agentConfig = JsonSerializer.Deserialize<Dictionary<string, object?>>(agentJson);
                if (agentConfig is not null)
                    return Task.FromResult<Dictionary<string, object?>?>(agentConfig);
            }
            catch
            {
                // ignore parse errors
            }
        }

        // 向后兼容：从 model 字段构建 agent 配置
        var model = GetConfigValue("model", fileValues);
        if (!string.IsNullOrWhiteSpace(model))
        {
            var fallbackConfig = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = model,
            };
            return Task.FromResult<Dictionary<string, object?>?>(fallbackConfig);
        }

        return Task.FromResult<Dictionary<string, object?>?>(null);
    }

    public Task<Dictionary<string, Dictionary<string, object?>>> GetStageAgentConfigsAsync()
    {
        var fileValues = ReadConfigFile();
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        var stageAgentsJson = GetConfigValue("stageAgents", fileValues);
        if (!string.IsNullOrWhiteSpace(stageAgentsJson))
        {
            try
            {
                var stageAgents = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(stageAgentsJson);
                if (stageAgents is not null)
                {
                    foreach (var (stage, config) in stageAgents)
                        result[stage] = new Dictionary<string, object?>(config, StringComparer.Ordinal);
                }
            }
            catch
            {
                // ignore parse errors
            }
        }

        // Legacy compatibility: stageModels maps to per-stage agent.model overrides.
        var stageModelsJson = GetConfigValue("stageModels", fileValues);
        if (!string.IsNullOrWhiteSpace(stageModelsJson))
        {
            try
            {
                var stageModels = JsonSerializer.Deserialize<Dictionary<string, string>>(stageModelsJson);
                if (stageModels is not null)
                {
                    foreach (var (stage, model) in stageModels)
                    {
                        if (string.IsNullOrWhiteSpace(model)) continue;
                        if (!result.TryGetValue(stage, out var config))
                        {
                            config = new Dictionary<string, object?>(StringComparer.Ordinal);
                            result[stage] = config;
                        }
                        config["model"] = model;
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }
        }

        return Task.FromResult(result);
    }

    public async Task SetAgentModelAsync(string? model)
    {
        var agent = await GetAgentConfigAsync() ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(model))
            agent.Remove("model");
        else
            agent["model"] = model;

        if (agent.Count == 0)
            await ClearAsync("agent");
        else
            await SetAsync("agent", agent);

        // Keep legacy model from shadowing the unified agent config.
        await ClearAsync("model");
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

    /// <summary>
    /// 读取配置文件，返回扁平化的 key-value 字典。
    /// </summary>
    private Dictionary<string, JsonNode?> ReadConfigFile()
    {
        if (!File.Exists(_configPath))
            return new Dictionary<string, JsonNode?>();

        try
        {
            var json = File.ReadAllText(_configPath);
            var cleaned = StripJsoncComments(json);
            var doc = JsonDocument.Parse(cleaned);
            var result = new Dictionary<string, JsonNode?>();
            FlattenJson(doc.RootElement, "", result);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read config file {Path}", _configPath);
            return new Dictionary<string, JsonNode?>();
        }
    }

    private async Task WriteConfigFileAsync(string key, string? value)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        JsonObject? root;
        if (File.Exists(_configPath))
        {
            var json = await File.ReadAllTextAsync(_configPath);
            var cleaned = StripJsoncComments(json);
            try
            {
                root = JsonNode.Parse(cleaned)?.AsObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        root ??= new JsonObject();

        // 将扁平 key 转换为嵌套结构：Mohist:Config:model → { "Mohist": { "Config": { "model": value } } }
        var path = new[] { "Mohist", "Config", key };
        JsonNode? nodeValue = null;
        if (value is not null)
        {
            try
            {
                nodeValue = JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                nodeValue = JsonValue.Create(value);
            }
        }
        SetNestedValue(root, path, nodeValue);

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(_configPath, root.ToJsonString(options));
    }

    private static void SetNestedValue(JsonObject root, string[] path, JsonNode? value)
    {
        var current = (JsonNode)root;
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(path[i], out var child) && child is JsonObject childObj)
            {
                current = childObj;
            }
            else if (current is JsonObject obj2)
            {
                var newObj = new JsonObject();
                obj2[path[i]] = newObj;
                current = newObj;
            }
            else
            {
                break;
            }
        }

        if (current is JsonObject target)
        {
            var lastKey = path[^1];
            if (value is null)
                target.Remove(lastKey);
            else
                target[lastKey] = value;
        }
    }

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, JsonNode?> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var newKey = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}:{property.Name}";
                    if (property.Value.ValueKind == JsonValueKind.Object || property.Value.ValueKind == JsonValueKind.Array)
                    {
                        result[newKey] = JsonNode.Parse(property.Value.GetRawText());
                        FlattenJson(property.Value, newKey, result);
                    }
                    else
                        result[newKey] = JsonNode.Parse(property.Value.GetRawText());
                }
                break;
            case JsonValueKind.Array:
                result[prefix] = JsonNode.Parse(element.GetRawText());
                break;
            default:
                result[prefix] = JsonNode.Parse(element.GetRawText());
                break;
        }
    }

    /// <summary>
    /// 配置优先级：环境变量 > config.jsonc > 默认值
    /// </summary>
    private string? GetConfigValue(string key, Dictionary<string, JsonNode?> fileValues)
    {
        // 优先级 1: 环境变量
        var envKey = $"MOHIST__CONFIG__{key.ToUpperInvariant()}";
        var envValue = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue;

        // 优先级 2: config.jsonc 文件
        var fileKey = $"Mohist:Config:{key}";
        if (fileValues.TryGetValue(fileKey, out var fileValue) && fileValue != null)
            return fileValue.ToString();

        // 优先级 3: IConfiguration（appsettings 等）
        var configValue = _configuration[$"Mohist:Config:{key}"];
        if (!string.IsNullOrWhiteSpace(configValue))
            return configValue;

        return null;
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

    private static string StripJsoncComments(string json)
    {
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < json.Length)
        {
            if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '*')
            {
                i += 2;
                while (i < json.Length - 1 && !(json[i] == '*' && json[i + 1] == '/'))
                    i++;
                i += 2;
                continue;
            }

            if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n')
                    i++;
                continue;
            }

            if (json[i] == '"')
            {
                result.Append(json[i]);
                i++;
                while (i < json.Length)
                {
                    result.Append(json[i]);
                    if (json[i] == '\\' && i + 1 < json.Length)
                    {
                        i++;
                        result.Append(json[i]);
                    }
                    else if (json[i] == '"')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            result.Append(json[i]);
            i++;
        }

        return result.ToString();
    }
}
