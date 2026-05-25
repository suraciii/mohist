using System.Text.Json;

namespace Mohist.Runner.Actions;

public class OpenSpecTasksAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var path = JsonInputs.String(context.With, "path");
        if (string.IsNullOrWhiteSpace(path))
            return new ActionResult("failure", "OpenSpec task loader requires 'path'");

        var fullPath = ResolvePath(context.WorkDir, path);
        if (!File.Exists(fullPath))
            return new ActionResult("failure", $"tasks.json not found: {path}");

        await using var stream = File.OpenRead(fullPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: context.CancellationToken);
        if (!document.RootElement.TryGetProperty("tasks", out var tasksElement) || tasksElement.ValueKind != JsonValueKind.Array)
            return new ActionResult("failure", "tasks.json must contain a tasks array");

        var templateTask = JsonInputs.Element(context.With, "task");
        var defaultUses = ReadNestedString(templateTask, "uses") ?? "mohist/agent";
        var defaultWith = ReadNestedObject(templateTask, "with");

        var tasks = new List<LoadedTask>();
        foreach (var item in tasksElement.EnumerateArray())
        {
            var id = ReadString(item, "id") ?? ReadString(item, "taskId");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var title = ReadString(item, "title") ?? id;
            var uses = ReadString(item, "uses") ?? defaultUses;
            var with = MergeWith(defaultWith, item);
            tasks.Add(new LoadedTask(id, title, uses, with));
        }

        var output = JsonSerializer.Serialize(new { tasks }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return new ActionResult("loaded", $"Loaded {tasks.Count} tasks", output);
    }

    private static string ResolvePath(string workDir, string path) => Path.IsPathRooted(path)
        ? path
        : Path.GetFullPath(Path.Combine(workDir, path));

    private static string? ReadString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

    private static string? ReadNestedString(JsonElement? item, string name) => item is { ValueKind: JsonValueKind.Object }
        && item.Value.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, JsonElement?>? ReadNestedObject(JsonElement? item, string name)
    {
        if (item is not { ValueKind: JsonValueKind.Object } || !item.Value.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(value.GetRawText());
    }

    private static Dictionary<string, JsonElement?>? MergeWith(Dictionary<string, JsonElement?>? defaultWith, JsonElement task)
    {
        var merged = defaultWith is null
            ? new Dictionary<string, JsonElement?>()
            : defaultWith.ToDictionary(kv => kv.Key, kv => kv.Value);

        AddString(merged, task, "description");
        AddElement(merged, task, "acceptanceCriteria", "acceptanceCriteria");
        AddElement(merged, task, "dependsOn", "dependsOn");
        AddString(merged, task, "priority");
        AddString(merged, task, "mode");
        AddString(merged, task, "type");
        AddElement(merged, task, "output", "output");
        AddElement(merged, task, "requireFiles", "requireFiles");
        AddElement(merged, task, "requireMarkers", "requireMarkers");

        if (task.TryGetProperty("with", out var withElement) && withElement.ValueKind == JsonValueKind.Object)
        {
            var taskWith = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(withElement.GetRawText());
            if (taskWith is not null)
            {
                foreach (var (key, value) in taskWith)
                    merged[key] = value?.Clone();
            }
        }

        return merged.Count == 0 ? null : merged;
    }

    private static void AddString(Dictionary<string, JsonElement?> target, JsonElement source, string name)
    {
        var value = ReadString(source, name);
        if (!string.IsNullOrWhiteSpace(value))
            target[name] = JsonSerializer.SerializeToElement(value);
    }

    private static void AddElement(Dictionary<string, JsonElement?> target, JsonElement source, string sourceName, string targetName)
    {
        if (source.TryGetProperty(sourceName, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            target[targetName] = value.Clone();
    }

    private sealed record LoadedTask(string Id, string Title, string? Uses, Dictionary<string, JsonElement?>? With);
}
