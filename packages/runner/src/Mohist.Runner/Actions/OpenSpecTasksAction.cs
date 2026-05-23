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
            tasks.Add(new LoadedTask(id, title, defaultUses, defaultWith));
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

    private sealed record LoadedTask(string Id, string Title, string? Uses, Dictionary<string, JsonElement?>? With);
}
