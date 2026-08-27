using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Workflow.Definition;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mohist.Server.Workflow.Services;

public static class WorkflowYamlSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = JSON.Options;

    public static WorkflowDefinition FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Workflow Definition JSON must be an object");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is "id" or "name" or "description" or "variables" or "defaults" or "artifacts")
                throw new InvalidOperationException($"Workflow Definition does not allow top-level field '{property.Name}'");
        }

        var stages = document.RootElement.TryGetProperty("stages", out var stagesElement)
            ? stagesElement.EnumerateArray().Select(ParseJsonStage).ToList()
            : null;
        if (stages is null)
            throw new InvalidOperationException("Workflow Definition JSON requires stages");

        var approval = document.RootElement.TryGetProperty("approval", out var approvalElement)
            ? approvalElement.Deserialize<ApprovalConfig>(JsonOptions)
            : null;
        var recoveries = document.RootElement.TryGetProperty("recoveries", out var recoveriesElement)
            ? recoveriesElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Object)
                .ToDictionary(p => p.Name, p => p.Value.Deserialize<RecoveryDefinition>(JsonOptions)!)
            : null;
        return new WorkflowDefinition(stages, approval, recoveries);
    }

    private static StageDefinition ParseJsonStage(JsonElement element)
    {
        if (element.TryGetProperty("variables", out _))
            throw new InvalidOperationException("Workflow Definition does not allow stage field 'variables'");

        return new StageDefinition(
            element.GetProperty("stage").GetString() ?? throw new InvalidOperationException("Workflow stage requires stage"),
            element.TryGetProperty("tasks", out var tasks)
                ? tasks.Deserialize<List<TaskDefinition>>(JsonOptions) ?? []
                : [],
            element.TryGetProperty("checks", out var checks)
                ? checks.Deserialize<List<CheckDefinition>>(JsonOptions) ?? []
                : [],
            element.TryGetProperty("requiresApproval", out var requiresApproval)
                && requiresApproval.ValueKind == JsonValueKind.True,
            LockBehavior: element.TryGetProperty("lockBehavior", out var lockBehavior)
                ? lockBehavior.GetString()
                : null,
            Resources: element.TryGetProperty("resources", out var resources)
                ? resources.Deserialize<List<string>>(JsonOptions)
                : null);
    }

    public static string ToJson(WorkflowDefinition definition) =>
        JsonSerializer.Serialize(definition, JsonOptions);

    public static WorkflowProfile FromProfileJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetString() ?? throw new InvalidOperationException("Workflow Profile requires id");
        var name = root.GetProperty("name").GetString() ?? throw new InvalidOperationException("Workflow Profile requires name");
        var description = root.GetProperty("description").GetString() ?? string.Empty;
        var definition = root.GetProperty("definition").GetRawText();
        return new WorkflowProfile(id, name, description, FromJson(definition));
    }

    public static WorkflowDefinition FromYaml(string yaml)
    {
        var result = WorkflowDefinitionParser.Parse(yaml);
        if (!result.IsValid)
            throw new InvalidOperationException(FormatErrors(result.Errors));
        return result.Definition!;
    }

    private static string FormatErrors(IReadOnlyList<ValidationError> errors) =>
        string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"));

    public static string ToYaml(WorkflowDefinition definition)
    {
        var document = new Dictionary<string, object?>
        {
            ["stages"] = definition.Stages.Select(ToStageMap).ToList(),
        };

        var approvalMap = ToApprovalMap(definition.Approval);
        if (approvalMap is not null) document["approval"] = approvalMap;

        var recoveriesMap = ToRecoveriesMap(definition.Recoveries);
        if (recoveriesMap is not null) document["recoveries"] = recoveriesMap;

        return CreateSerializer().Serialize(document);
    }

    private static ISerializer CreateSerializer() => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static Dictionary<string, object?> ToStageMap(StageDefinition stage)
    {
        var map = new Dictionary<string, object?>
        {
            ["stage"] = stage.Stage,
            ["tasks"] = stage.Tasks.Select(ToTaskMap).ToList(),
            ["checks"] = stage.Checks.Select(ToCheckMap).ToList(),
        };
        if (stage.RequiresApproval) map["requiresApproval"] = true;
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
        map["recovery"] = ToRecoveryMap(recovery);
    }

    private static Dictionary<string, object?> ToRecoveryMap(RecoveryDefinition recovery)
    {
        return new Dictionary<string, object?>
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

    private static Dictionary<string, object?>? ToRecoveriesMap(IReadOnlyDictionary<string, RecoveryDefinition>? recoveries)
    {
        if (recoveries is null || recoveries.Count == 0) return null;
        return recoveries.ToDictionary(
            kv => kv.Key,
            kv => (object?)ToRecoveryMap(kv.Value));
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
            ["id"] = check.Id,
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

    private static Dictionary<string, object?>? ObjectMap(Dictionary<string, JsonElement?>? map)
    {
        return map?.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? JsonToObject(kv.Value.Value) : null);
    }

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
