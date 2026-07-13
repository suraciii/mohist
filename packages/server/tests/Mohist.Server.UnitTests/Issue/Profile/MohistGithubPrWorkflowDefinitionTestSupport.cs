using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

internal static class MohistGithubPrWorkflowDefinitionTestSupport
{
    internal static IEnumerable<Mohist.Server.Workflow.Domain.Definition.TaskDefinition> CollectAllTasks(
        Mohist.Server.Workflow.Domain.Definition.StageDefinition stage)
    {
        foreach (var task in stage.Tasks)
            foreach (var visited in CollectWithNested(task))
                yield return visited;
    }

    internal static IEnumerable<Mohist.Server.Workflow.Domain.Definition.TaskDefinition> CollectAllTasks(
        params Mohist.Server.Workflow.Domain.Definition.StageDefinition[] stages)
    {
        foreach (var stage in stages)
            foreach (var task in CollectAllTasks(stage))
                yield return task;
    }

    internal static IEnumerable<Mohist.Server.Workflow.Domain.Definition.TaskDefinition> CollectWithNested(
        Mohist.Server.Workflow.Domain.Definition.TaskDefinition task)
    {
        yield return task;
    }

    internal static JsonElement GetRecovery(Dictionary<string, JsonElement?> with)
    {
        var element = with["recovery"] ?? throw new InvalidOperationException("task 'with' is missing 'recovery'");
        return element;
    }

    internal static IEnumerable<string> ExtractRecoveryTaskIds(RecoveryDefinition recovery)
    {
        foreach (var handler in recovery.Handlers)
        {
            foreach (var task in handler.Tasks)
            {
                yield return task.Id;
            }
        }
    }

    internal static IEnumerable<JsonElement> EnumerateRecoveryTaskElements(
        Mohist.Server.Workflow.Domain.Definition.StageDefinition stage)
    {
        foreach (var task in stage.Tasks)
        {
            if (task.With is null) continue;
            if (!task.With.TryGetValue("recovery", out var recoveryEl) || recoveryEl is null) continue;
            var recovery = recoveryEl.Value;
            if (recovery.ValueKind != JsonValueKind.Object) continue;
            if (!recovery.TryGetProperty("handlers", out var handlers)) continue;
            foreach (var handler in handlers.EnumerateArray())
            {
                if (!handler.TryGetProperty("tasks", out var tasks)) continue;
                foreach (var t in tasks.EnumerateArray())
                    yield return t;
            }
        }
    }

    internal static bool? ReadBoolWith(Mohist.Server.Workflow.Domain.Definition.TaskDefinition task, string key)
    {
        if (task.With is null || !task.With.TryGetValue(key, out var element) || element is null) return null;
        if (element.Value.ValueKind == JsonValueKind.True) return true;
        if (element.Value.ValueKind == JsonValueKind.False) return false;
        if (element.Value.ValueKind == JsonValueKind.String)
        {
            var text = element.Value.GetString();
            if (text is null) return null;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return element.Value.GetBoolean();
    }

    internal static string? ReadStringWith(Mohist.Server.Workflow.Domain.Definition.TaskDefinition task, string key)
    {
        if (task.With is null || !task.With.TryGetValue(key, out var element) || element is null) return null;
        return element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : element.Value.GetRawText();
    }

    internal static string? ReadStringWith(Mohist.Server.Workflow.Domain.Definition.CheckDefinition check, string key)
    {
        if (check.With is null || !check.With.TryGetValue(key, out var element) || element is null) return null;
        return element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : element.Value.GetRawText();
    }

    internal static Dictionary<string, object?>? GetMap(Dictionary<string, JsonElement?> with, string key)
    {
        if (!with.TryGetValue(key, out var element) || element is null) return null;
        var json = element.Value.GetRawText();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
    }

    internal static List<object?>? GetList(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            List<object?> list => list,
            JsonElement element when element.ValueKind == JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(element.GetRawText()),
            _ => null,
        };
    }

    internal static Dictionary<string, object?> NormalizeToMap(object? value) => value switch
    {
        Dictionary<string, object?> map => map,
        JsonElement element => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())
            ?? new Dictionary<string, object?>(),
        _ => new Dictionary<string, object?>(),
    };

    internal static string[] ExtractOneOfTexts(Dictionary<string, object?> marker)
    {
        if (!marker.TryGetValue("oneOf", out var value) || value is null)
            return Array.Empty<string>();
        return value switch
        {
            IEnumerable<object?> enumerable => enumerable.Select(o => o?.ToString() ?? "").ToArray(),
            JsonElement element when element.ValueKind == JsonValueKind.Array => element.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                .ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    internal static void AssertTaskWithMapsMatchExcept(
        Mohist.Server.Workflow.Domain.Definition.TaskDefinition expected,
        Mohist.Server.Workflow.Domain.Definition.TaskDefinition actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected.With), JsonSerializer.Serialize(actual.With));
    }

}
