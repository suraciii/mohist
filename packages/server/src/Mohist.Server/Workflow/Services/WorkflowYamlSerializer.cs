using System.Globalization;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Definition;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowYamlSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = JSON.Options;

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
            Description: NullIfEmpty(String(document, "description")),
            Variables: JsonElementMap(OptionalMap(document, "variables")),
            Defaults: JsonElementMap(OptionalMap(document, "defaults")),
            Artifacts: OptionalMap(document, "artifacts")?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
            Approval: ToApproval(OptionalMap(document, "approval")));
    }

    public static string ToYaml(WorkflowDefinition definition)
    {
        var document = new Dictionary<string, object?>
        {
            ["id"] = definition.Id,
            ["name"] = definition.Name,
            ["description"] = definition.Description,
            ["variables"] = ObjectMap(definition.Variables),
            ["defaults"] = ObjectMap(definition.Defaults),
            ["artifacts"] = definition.Artifacts,
            ["stages"] = definition.Stages.Select(ToStageMap).ToList(),
        };

        var approvalMap = ToApprovalMap(definition.Approval);
        if (approvalMap is not null) document["approval"] = approvalMap;

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

        var uses = NullIfEmpty(String(map, "uses"));
        var withMap = OptionalMap(map, "with");
        var expectMap = OptionalMap(map, "expect");

        if (withMap is not null)
            ValidateTaskExpectations(id, uses, withMap);
        ValidateExpectVerdictMarkers(id, expectMap);

        var artifacts = ParseTaskArtifacts(map);
        var setVars = ParseTaskSetVars(map, id);
        var recovery = ParseTaskRecovery(map, id);

        return new TaskDefinition(id, title, uses, JsonElementMap(withMap), JsonElementMap(expectMap), artifacts, setVars, recovery);
    }

    private static void ValidateExpectVerdictMarkers(string taskId, Dictionary<string, object?>? expectMap)
    {
        if (expectMap is null) return;

        var markers = List(expectMap, "markers");
        foreach (var marker in markers)
        {
            var markerMap = Normalize(marker) as Dictionary<string, object?>;
            if (markerMap is null) continue;

            var contains = String(markerMap, "contains");
            if (IsVerdictMarker(contains))
                throw new InvalidOperationException(
                    $"Workflow task '{taskId}' declares verdict marker '{contains}' under 'expect.markers.contains'. " +
                    "Use 'oneOf' (not 'contains') for promise verdict markers under task-level 'expect', " +
                    "or move non-verdict literal markers into a check definition.");
        }
    }

    private static Dictionary<string, string>? ParseTaskSetVars(Dictionary<string, object?> taskMap, string taskId)
    {
        if (!taskMap.TryGetValue("setVars", out var setVarsValue) || setVarsValue is null)
            return null;

        var setVarsMap = Normalize(setVarsValue) as Dictionary<string, object?>;
        if (setVarsMap is null)
            throw new InvalidOperationException($"Workflow task '{taskId}' 'setVars' must be an object");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in setVarsMap)
        {
            var strValue = value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(strValue))
                throw new InvalidOperationException($"Workflow task '{taskId}' setVars entry '{key}' requires a non-empty value");
            result[key] = strValue;
        }

        return result.Count == 0 ? null : result;
    }

    private static RecoveryDefinition? ParseTaskRecovery(Dictionary<string, object?> taskMap, string taskId)
    {
        if (!taskMap.TryGetValue("recovery", out var recoveryValue) || recoveryValue is null)
            return null;

        var recoveryMap = Normalize(recoveryValue) as Dictionary<string, object?>;
        if (recoveryMap is null)
            throw new InvalidOperationException($"Workflow task '{taskId}' 'recovery' must be an object");

        var budget = 0;
        if (recoveryMap.TryGetValue("budget", out var budgetValue) && budgetValue is not null)
        {
            if (int.TryParse(budgetValue.ToString(), out var parsed))
                budget = parsed;
        }

        var handlers = new List<RecoveryHandlerDefinition>();
        var hasDefaultHandler = false;
        if (recoveryMap.TryGetValue("handlers", out var handlersValue) && handlersValue is not null)
        {
            var handlersList = handlersValue as List<object?>;
            if (handlersList is null)
                throw new InvalidOperationException($"Workflow task '{taskId}' recovery.handlers must be a list");

            for (var handlerIndex = 0; handlerIndex < handlersList.Count; handlerIndex++)
            {
                var handlerEntry = handlersList[handlerIndex];
                var handlerMap = Normalize(handlerEntry) as Dictionary<string, object?>;
                if (handlerMap is null) continue;

                var hasWhen = handlerMap.ContainsKey("when");
                var when = NullIfEmpty(String(handlerMap, "when"));
                if (hasWhen && when is null)
                    throw new InvalidOperationException($"Workflow task '{taskId}' recovery handler 'when' requires a non-empty field=value expression");
                if (when is null)
                {
                    if (hasDefaultHandler)
                        throw new InvalidOperationException($"Workflow task '{taskId}' recovery allows at most one default handler");
                    hasDefaultHandler = true;
                    if (handlerIndex != handlersList.Count - 1)
                        throw new InvalidOperationException($"Workflow task '{taskId}' recovery default handler must be last");
                }

                var handlerTasks = new List<TaskDefinition>();
                if (handlerMap.TryGetValue("tasks", out var tasksValue) && tasksValue is not null)
                {
                    var tasksList = tasksValue as List<object?>;
                    if (tasksList is not null)
                        foreach (var t in tasksList)
                            handlerTasks.Add(ToTask(t));
                }

                var retrySelf = handlerMap.TryGetValue("retrySelf", out var rs) && rs is bool b && b;
                handlers.Add(new RecoveryHandlerDefinition(when, handlerTasks, retrySelf));
            }
        }

        return new RecoveryDefinition(budget, handlers);
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

    private static void ValidateTaskExpectations(string taskId, string? uses, Dictionary<string, object?> withMap)
    {
        if (!IsInlineAgentUses(uses))
            return;

        if (withMap.TryGetValue("expect", out var expectValue) && expectValue is not null)
        {
            if (HasLegacyCompletionPolicyShape(expectValue))
                throw new InvalidOperationException(
                    $"Workflow task '{taskId}' declares Workflow completion policy under 'with.expect'. " +
                    "Move 'files', 'markers', and 'failIf' to task-level 'expect'. " +
                    "'with.expect' is reserved for Action-owned input on the selected Action contract.");
        }

        if (withMap.ContainsKey("agent"))
        {
            throw new InvalidOperationException(
                $"Workflow task '{taskId}' declares legacy agent configuration under 'with.agent'. " +
                "Bind the selected Action's 'options' explicitly, e.g. 'options: ${{{{ vars.agent }}}}'.");
        }

        // Spec scenario "Legacy agent input is invalid": inline-agent tasks
        // MUST NOT carry legacy execution-backend discriminators `kind` or
        // `type` inside `with`. Only `agent` and Workflow completion policy
        // have their own actionable errors above; `kind`/`type` get a
        // shared message that names the offending field.
        if (withMap.ContainsKey("kind"))
        {
            throw new InvalidOperationException(
                $"Workflow task '{taskId}' declares legacy execution discriminator 'with.kind'. " +
                "The 'mohist/opencode' Action is selected by 'uses' and does not read 'kind'. " +
                "Remove 'with.kind'; if model configuration is intended, bind 'options: ${{{{ vars.agent }}}}'.");
        }

        if (withMap.ContainsKey("type"))
        {
            throw new InvalidOperationException(
                $"Workflow task '{taskId}' declares legacy execution discriminator 'with.type'. " +
                "The 'mohist/opencode' Action is selected by 'uses' and does not read 'type'. " +
                "Remove 'with.type'; if model configuration is intended, bind 'options: ${{{{ vars.agent }}}}'.");
        }
    }

    private static bool HasLegacyCompletionPolicyShape(object? expectValue)
    {
        var expect = Normalize(expectValue) as Dictionary<string, object?>;
        if (expect is null) return false;

        if (expect.TryGetValue("files", out var filesValue) && filesValue is not null) return true;
        if (expect.TryGetValue("markers", out var markersValue) && markersValue is not null) return true;
        if (expect.TryGetValue("failIf", out var failIfValue)
            && failIfValue is not null
            && !string.IsNullOrWhiteSpace(failIfValue.ToString()))
            return true;
        return false;
    }

    private static bool IsInlineAgentUses(string? uses) =>
        string.Equals(uses, "mohist/opencode", StringComparison.Ordinal);

    private static bool IsVerdictMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "PASS" or "FAIL"
            || normalized.Contains("<PROMISE>PASS</PROMISE>", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("<PROMISE>FAIL</PROMISE>", StringComparison.OrdinalIgnoreCase);
    }

    private static ApprovalConfig? ToApproval(Dictionary<string, object?>? map)
    {
        if (map is null) return null;
        var feedbackMap = OptionalMap(map, "feedback");
        if (feedbackMap is null) return null;
        var tasks = List(feedbackMap, "tasks").Select(ToTask).ToList();
        if (tasks.Count == 0)
            throw new InvalidOperationException("Workflow approval.feedback requires at least one task");
        return new ApprovalConfig(new ApprovalFeedbackConfig(tasks));
    }

    private static Dictionary<string, object?>? ToApprovalMap(ApprovalConfig? approval)
    {
        if (approval is null || approval.Feedback is null) return null;
        var tasks = approval.Feedback.Tasks;
        if (tasks is null || tasks.Count == 0) return null;
        return new Dictionary<string, object?>
        {
            ["feedback"] = new Dictionary<string, object?>
            {
                ["tasks"] = tasks.Select(ToTaskMap).ToList()
            }
        };
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

        if (map.ContainsKey("repairLimit") || map.ContainsKey("repairTask"))
            throw new InvalidOperationException(
                $"Workflow check '{name}' uses obsolete check-level repair. Move this verification into a task and use task-level recovery.");

        return new CheckDefinition(
            name,
            title,
            NullIfEmpty(String(map, "uses")),
            JsonElementMap(OptionalMap(map, "with")));
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
        AddExpect(map, task.Expect);
        AddArtifacts(map, task.Artifacts);
        AddSetVars(map, task.SetVars);
        AddRecovery(map, task.Recovery);
        return map;
    }

    private static void AddSetVars(Dictionary<string, object?> map, Dictionary<string, string>? setVars)
    {
        if (setVars is null || setVars.Count == 0) return;
        map["setVars"] = setVars.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
    }

    private static void AddRecovery(Dictionary<string, object?> map, RecoveryDefinition? recovery)
    {
        if (recovery is null) return;
        map["recovery"] = new Dictionary<string, object?>
        {
            ["budget"] = recovery.Budget,
            ["handlers"] = recovery.Handlers.Select(h =>
            {
                var handler = new Dictionary<string, object?>
                {
                    ["tasks"] = h.Tasks.Select(ToTaskMap).ToList(),
                    ["retrySelf"] = h.RetrySelf,
                };
                if (h.When is not null) handler["when"] = h.When;
                return (object?)handler;
            }).ToList(),
        };
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
        return map;
    }

    private static void AddWith(Dictionary<string, object?> map, Dictionary<string, JsonElement?>? with)
    {
        var values = ObjectMap(with);
        if (values is not null) map["with"] = values;
    }

    private static void AddExpect(Dictionary<string, object?> map, Dictionary<string, JsonElement?>? expect)
    {
        var values = ObjectMap(expect);
        if (values is not null) map["expect"] = values;
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
