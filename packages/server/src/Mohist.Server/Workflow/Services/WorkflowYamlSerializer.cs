using System.Globalization;
using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowYamlSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WorkflowDefinition FromYaml(string yaml, string id = "workflow")
    {
        var document = Normalize(CreateDeserializer().Deserialize<Dictionary<object, object?>>(yaml)) as Dictionary<string, object?>
            ?? throw new InvalidOperationException("Workflow YAML is empty");

        var workflowId = String(document, "id");
        var stages = List(document, "stages").Select(ToStage).ToList();
        if (stages.Count == 0)
            throw new InvalidOperationException("Workflow YAML requires at least one stage");

        return new WorkflowDefinition(
            string.IsNullOrWhiteSpace(workflowId) ? id : workflowId,
            stages,
            Name: NullIfEmpty(String(document, "name")),
            Variables: JsonElementMap(OptionalMap(document, "variables")),
            Defaults: JsonElementMap(OptionalMap(document, "defaults")),
            Artifacts: OptionalMap(document, "artifacts")?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""));
    }

    public static string ToYaml(WorkflowDefinition definition)
    {
        var document = new Dictionary<string, object?>
        {
            ["id"] = definition.Id,
            ["name"] = definition.Name,
            ["variables"] = ObjectMap(definition.Variables),
            ["defaults"] = ObjectMap(definition.Defaults),
            ["artifacts"] = definition.Artifacts,
            ["stages"] = definition.Stages.Select(ToStageMap).ToList(),
        };

        return CreateSerializer().Serialize(document);
    }

    private static IDeserializer CreateDeserializer() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static ISerializer CreateSerializer() => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static StageDefinition ToStage(object? value)
    {
        var map = Map(value, "stage");
        var stage = String(map, "stage");
        if (string.IsNullOrWhiteSpace(stage))
            throw new InvalidOperationException("Workflow stage requires stage");

        return new StageDefinition(
            stage,
            List(map, "tasks").Select(ToTask).ToList(),
            List(map, "checks").Select(ToCheck).ToList(),
            Bool(map, "requiresApproval"),
            Variables: JsonElementMap(OptionalMap(map, "variables")),
            LockBehavior: NullIfEmpty(String(map, "lockBehavior")),
            Resources: StringList(map, "resources"));
    }

    private static TaskDefinition ToTask(object? value)
    {
        var map = Map(value, "task");
        var id = String(map, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Workflow task requires id");

        var title = String(map, "title");
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException($"Workflow task {id} requires title");

        var withMap = OptionalMap(map, "with");
        if (withMap is not null)
            ValidateTaskExpectations(id, withMap);

        var artifacts = ParseTaskArtifacts(map);

        return new TaskDefinition(id, title, NullIfEmpty(String(map, "uses")), JsonElementMap(withMap), artifacts);
    }

    private static TaskArtifactCapture? ParseTaskArtifacts(Dictionary<string, object?> taskMap)
    {
        if (!taskMap.TryGetValue("artifacts", out var artifactsValue) || artifactsValue is null)
            return null;

        var artifactsMap = Normalize(artifactsValue) as Dictionary<string, object?>;
        if (artifactsMap is null)
            throw new InvalidOperationException("Workflow task 'artifacts' must be an object");

        var filesValue = artifactsMap.TryGetValue("files", out var files) ? files : null;
        var filesList = filesValue is null ? [] : List(artifactsMap, "files");
        var declarations = new List<TaskArtifactDeclaration>(filesList.Count);
        foreach (var item in filesList)
        {
            var entry = Normalize(item);
            string? path = entry switch
            {
                string s => NullIfEmpty(s),
                Dictionary<string, object?> obj => obj.TryGetValue("path", out var p) ? NullIfEmpty(p?.ToString() ?? "") : null,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Workflow task 'artifacts.files' entries require a 'path'");
            declarations.Add(new TaskArtifactDeclaration(path!));
        }

        return declarations.Count == 0 ? null : new TaskArtifactCapture(declarations);
    }

    private static void ValidateTaskExpectations(string taskId, Dictionary<string, object?> withMap)
    {
        var expect = OptionalMap(withMap, "expect");
        if (expect is null) return;

        var markers = List(expect, "markers");
        foreach (var marker in markers)
        {
            var markerMap = Normalize(marker) as Dictionary<string, object?>;
            if (markerMap is null) continue;

            var contains = String(markerMap, "contains");
            if (IsVerdictMarker(contains))
                throw new InvalidOperationException(
                    $"Workflow task '{taskId}' configures a verdict marker ({contains}) as an artifact expectation. " +
                    "Move verdict marker requirements into a check definition.");
        }
    }

    private static bool IsVerdictMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "PASS" or "FAIL" ||
               normalized.Contains("<PROMISE>PASS</PROMISE>", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("<PROMISE>FAIL</PROMISE>", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("PASS", StringComparison.OrdinalIgnoreCase) &&
               (normalized.Contains("<PROMISE>", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("</PROMISE>", StringComparison.OrdinalIgnoreCase));
    }

    private static CheckDefinition ToCheck(object? value)
    {
        var map = Map(value, "check");
        var name = String(map, "name");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Workflow check requires name");

        var title = String(map, "title");
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException($"Workflow check {name} requires title");

        var repairLimit = Int(map, "repairLimit");
        var repairTask = map.TryGetValue("repairTask", out var repairTaskValue) && repairTaskValue is not null
            ? ToTask(repairTaskValue)
            : null;
        var verifyTask = map.TryGetValue("verifyTask", out var verifyTaskValue) && verifyTaskValue is not null
            ? ToTask(verifyTaskValue)
            : null;

        return new CheckDefinition(
            name,
            title,
            NullIfEmpty(String(map, "uses")),
            JsonElementMap(OptionalMap(map, "with")),
            repairLimit > 0 && repairTask is not null ? new CheckFailureAction(new CheckFailureRepair(repairLimit, repairTask, verifyTask)) : null);
    }

    private static Dictionary<string, object?> ToStageMap(StageDefinition stage)
    {
        var map = new Dictionary<string, object?>
        {
            ["stage"] = stage.Stage,
            ["tasks"] = stage.Tasks.Select(ToTaskMap).ToList(),
            ["checks"] = stage.Checks.Select(ToCheckMap).ToList(),
        };
        if (stage.RequiresApproval) map["requiresApproval"] = true;
        if (stage.Variables is not null) map["variables"] = ObjectMap(stage.Variables);
        if (!string.IsNullOrWhiteSpace(stage.LockBehavior)) map["lockBehavior"] = stage.LockBehavior;
        if (stage.Resources is { Count: > 0 }) map["resources"] = stage.Resources;
        return map;
    }

    private static Dictionary<string, object?> ToTaskMap(TaskDefinition task)
    {
        var map = new Dictionary<string, object?>
        {
            ["id"] = task.Id,
            ["title"] = task.Title,
        };
        if (task.Uses is not null) map["uses"] = task.Uses;
        AddWith(map, task.With);
        AddArtifacts(map, task.Artifacts);
        return map;
    }

    private static void AddArtifacts(Dictionary<string, object?> map, TaskArtifactCapture? artifacts)
    {
        if (artifacts is null || artifacts.IsEmpty) return;
        var files = artifacts.Files.Select(f => (object?)new Dictionary<string, object?> { ["path"] = f.Path }).ToList();
        map["artifacts"] = new Dictionary<string, object?> { ["files"] = files };
    }

    private static Dictionary<string, object?> ToCheckMap(CheckDefinition check)
    {
        var map = new Dictionary<string, object?>
        {
            ["name"] = check.Name,
            ["title"] = check.Title,
        };
        if (check.Uses is not null) map["uses"] = check.Uses;
        AddWith(map, check.With);
        if (check.OnFailure?.Repair is { } repair)
        {
            map["repairLimit"] = repair.Limit;
            map["repairTask"] = ToTaskMap(repair.Task);
            if (repair.VerifyTask is not null)
                map["verifyTask"] = ToTaskMap(repair.VerifyTask);
        }
        return map;
    }

    private static void AddWith(Dictionary<string, object?> map, Dictionary<string, JsonElement?>? with)
    {
        var values = ObjectMap(with);
        if (values is not null) map["with"] = values;
    }

    private static Dictionary<string, JsonElement?>? JsonElementMap(Dictionary<string, object?>? map)
    {
        return map?.ToDictionary(kv => kv.Key, kv => (JsonElement?)JsonSerializer.SerializeToElement(kv.Value, JsonOptions));
    }

    private static Dictionary<string, object?>? ObjectMap(Dictionary<string, JsonElement?>? map)
    {
        return map?.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? JsonToObject(kv.Value.Value) : null);
    }

    private static Dictionary<string, object?> Map(object? value, string name)
    {
        return Normalize(value) as Dictionary<string, object?>
            ?? throw new InvalidOperationException($"Workflow YAML {name} must be an object");
    }

    private static Dictionary<string, object?>? OptionalMap(IReadOnlyDictionary<string, object?> map, string key)
    {
        return map.TryGetValue(key, out var value) && value is not null
            ? Map(value, key)
            : null;
    }

    private static List<object?> List(IReadOnlyDictionary<string, object?> map, string key)
    {
        return map.TryGetValue(key, out var value) && Normalize(value) is List<object?> list ? list : [];
    }

    private static List<string>? StringList(IReadOnlyDictionary<string, object?> map, string key)
    {
        var values = List(map, key)
            .Select(v => v?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToList();
        return values.Count == 0 ? null : values;
    }

    private static string String(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";

    private static bool Bool(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is bool flag && flag;

    private static int Int(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null && int.TryParse(value.ToString(), out var number) ? number : 0;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static object? Normalize(object? value) => value switch
    {
        Dictionary<object, object?> map => map.ToDictionary(kv => kv.Key.ToString() ?? "", kv => Normalize(kv.Value)),
        Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)),
        IList<object?> list => list.Select(Normalize).ToList(),
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        string text when bool.TryParse(text, out var flag) => flag,
        _ => value,
    };

    private static object? JsonToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(JsonToObject).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
        _ => null,
    };
}
