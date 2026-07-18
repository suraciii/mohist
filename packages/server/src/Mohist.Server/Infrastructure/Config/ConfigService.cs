using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Infrastructure.Config;

public class ConfigService : ISingletonService
{
    private readonly IConfiguration _configuration;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly ILogger<ConfigService> _log;
    private readonly IConfigDocumentStore _documents;

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
        ["agent"] = ("json", null),
        ["stageAgents"] = ("json", null),
        ["logLevel"] = ("string", "INFO"),
    };

    public ConfigService(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        ILogger<ConfigService> log,
        IConfigDocumentStore documents)
    {
        _configuration = configuration;
        _environment = environment;
        _log = log;
        _documents = documents;
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

        var (valid, error) = Validate(key, ToComparableString(value));
        if (!valid)
            throw new InvalidOperationException(error!);

        var strValue = value switch
        {
            null => "",
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JSON.Serialize(value),
        };

        await WriteConfigFileAsync(key, strValue);
        _log.LogInformation("Config {Key} updated in {Path}", key, _documents.Location);
    }

    private static string ToComparableString(object value) => value switch
    {
        null => "",
        JsonElement json => json.ValueKind == JsonValueKind.String
            ? (json.GetString() ?? "")
            : json.GetRawText(),
        string text => text,
        _ => JSON.Serialize(value),
    };

    public async Task ClearAsync(string key)
    {
        if (!_schema.ContainsKey(key))
            throw new InvalidOperationException($"Unknown config key: {key}");

        await WriteConfigFileAsync(key, null);
        _log.LogInformation("Config {Key} cleared from {Path}", key, _documents.Location);
    }

    /// <summary>
    /// Returns the global default agent configuration read from the <c>agent</c>
    /// object in <c>config.jsonc</c>, projected down to the converged
    /// <c>{model, variant}</c> whitelist so legacy ACP/liveness keys never
    /// enter <c>vars.agent</c> from this write path. Returns <c>null</c>
    /// when no <c>agent</c> object is configured or when no allowed key
    /// survives the projection.
    /// </summary>
    public Task<Dictionary<string, object?>?> GetAgentConfigAsync()
    {
        var fileValues = ReadConfigFile();

        var agentJson = GetConfigValue("agent", fileValues);
        if (string.IsNullOrWhiteSpace(agentJson))
            return Task.FromResult<Dictionary<string, object?>?>(null);

        try
        {
            var agentConfig = JSON.Deserialize<Dictionary<string, object?>>(agentJson);
            return Task.FromResult<Dictionary<string, object?>?>(AgentConfigSchema.Filter(agentConfig));
        }
        catch
        {
            // ignore parse errors
            return Task.FromResult<Dictionary<string, object?>?>(null);
        }
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
                var stageAgents = JSON.Deserialize<Dictionary<string, Dictionary<string, object?>>>(stageAgentsJson);
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

        return Task.FromResult(result);
    }

    /// <summary>
    /// Global config.jsonc expressed as a <see cref="VariableBundle"/> for use as the
    /// global layer in the T1 (issue creation) merge. <c>vars.agent</c> is populated
    /// from the existing <see cref="GetAgentConfigAsync"/> output. <c>stages</c> is
    /// always empty because stage names are project-specific and cannot be configured
    /// globally.
    /// </summary>
    public async Task<VariableBundle> GetVariables()
    {
        var agent = await GetAgentConfigAsync();
        if (agent is null || agent.Count == 0)
            return VariableBundle.Empty;

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agent"] = agent,
        };

        var varsElement = JSON.SerializeToElement(vars);
        return new VariableBundle(Vars: varsElement, Stages: null);
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
    }

    private static readonly HashSet<string> SupportedLogLevels = new(StringComparer.Ordinal)
    {
        "DEBUG",
        "INFO",
        "WARN",
        "ERROR",
    };

    public (bool valid, string? error) Validate(string key, string value)
    {
        if (!_schema.TryGetValue(key, out var def))
            return (false, $"Unknown config key: {key}");

        var (type, _) = def;
        if (string.Equals(key, "logLevel", StringComparison.Ordinal))
        {
            if (!SupportedLogLevels.Contains(value))
                return (false, $"logLevel must be one of DEBUG, INFO, WARN, ERROR");
            return (true, null);
        }
        return type switch
        {
            "number" => int.TryParse(value, out _) ? (true, null) : (false, $"{key} must be a number"),
            "json" => JsonIsValid(value) ? (true, null) : (false, $"{key} must be valid JSON"),
            _ => (true, null),
        };
    }

    public static IReadOnlyCollection<string> GetSupportedLogLevels() => SupportedLogLevels;

    /// <summary>
    /// 读取配置文件，返回扁平化的 key-value 字典。
    /// </summary>
    private Dictionary<string, JsonNode?> ReadConfigFile()
    {
        try
        {
            var json = _documents.Read();
            if (json is null)
                return new Dictionary<string, JsonNode?>();
            var doc = JsonDocument.Parse(json, JsoncDocumentOptions);
            var result = new Dictionary<string, JsonNode?>();
            FlattenJson(doc.RootElement, "", result);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read config file {Path}", _documents.Location);
            return new Dictionary<string, JsonNode?>();
        }
    }

    private async Task WriteConfigFileAsync(string key, string? value)
    {
        JsonObject? root;
        var json = _documents.Read();
        if (json is not null)
        {
            try
            {
                root = JsonNode.Parse(json, documentOptions: JsoncDocumentOptions)?.AsObject();
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

        var options = JSON.Indented;
        await _documents.WriteAsync(root.ToJsonString(options)).ConfigureAwait(false);
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

    private static readonly JsonDocumentOptions JsoncDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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
                        result[newKey] = JsonNode.Parse(property.Value.GetRawText(), documentOptions: JsoncDocumentOptions);
                        FlattenJson(property.Value, newKey, result);
                    }
                    else
                        result[newKey] = JsonNode.Parse(property.Value.GetRawText(), documentOptions: JsoncDocumentOptions);
                }
                break;
            case JsonValueKind.Array:
                result[prefix] = JsonNode.Parse(element.GetRawText(), documentOptions: JsoncDocumentOptions);
                break;
            default:
                result[prefix] = JsonNode.Parse(element.GetRawText(), documentOptions: JsoncDocumentOptions);
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
        var envValue = _environment.GetEnvironmentVariable(envKey);
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
        "json" => JSON.DeserializeElement(value),
        _ => value,
    };

    private static bool JsonIsValid(string value)
    {
        try { JSON.DeserializeElement(value); return true; }
        catch { return false; }
    }
}
