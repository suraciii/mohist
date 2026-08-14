using System.Globalization;
using System.Text;
using Mohist.Workflow.Definition;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// deterministic canonical YAML renderer used by the
/// data migration to convert legacy semantic WorkflowProfile JSON into a
/// stable, persisted YAML source. The renderer is the bridge between
/// legacy <c>WorkflowProfilePersistence.Serialize</c> output (which keeps
/// the model in JSON) and the post-migration <c>Verbatim</c> /
/// <c>CanonicalLegacy</c> YAML contract. The output is byte-stable for a
/// given semantic input so verification tests can assert exact equality.
/// </summary>
internal static class WorkflowProfileCanonicalYamlRenderer
{
    public static string Render(WorkflowProfile profile)
    {
        var document = BuildDocument(profile);
        return CreateSerializer().Serialize(document);
    }

    private static ISerializer CreateSerializer() => new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static Dictionary<string, object?> BuildDocument(WorkflowProfile profile)
    {
        var document = new Dictionary<string, object?>
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
        };
        if (!string.IsNullOrEmpty(profile.Description))
            document["description"] = profile.Description;
        if (!string.IsNullOrEmpty(profile.AgentAction))
            document["agentAction"] = profile.AgentAction;
        if (profile.Definition is not null)
        {
            foreach (var entry in BuildDefinition(profile.Definition))
                document[entry.Key] = entry.Value;
        }
        return document;
    }

    private static Dictionary<string, object?> BuildDefinition(WorkflowDefinition definition)
    {
        var stages = definition.Stages
            .Select(stage => BuildStage(stage))
            .ToList();
        var doc = new Dictionary<string, object?>
        {
            ["stages"] = stages,
        };
        if (definition.Approval is not null)
            doc["approval"] = BuildApproval(definition.Approval);
        return doc;
    }

    private static Dictionary<string, object?> BuildStage(StageDefinition stage)
    {
        var map = new Dictionary<string, object?>
        {
            ["stage"] = stage.Stage,
            ["tasks"] = stage.Tasks.Select(BuildTask).ToList(),
            ["checks"] = stage.Checks.Select(BuildCheck).ToList(),
        };
        if (stage.RequiresApproval) map["requiresApproval"] = true;
        if (!string.IsNullOrWhiteSpace(stage.LockBehavior)) map["lockBehavior"] = stage.LockBehavior;
        if (stage.Resources is { Count: > 0 }) map["resources"] = stage.Resources;
        return map;
    }

    private static Dictionary<string, object?> BuildTask(TaskDefinition task)
    {
        var map = new Dictionary<string, object?>
        {
            ["id"] = task.Id,
            ["title"] = task.Title ?? task.Id,
        };
        if (!string.IsNullOrEmpty(task.Uses)) map["uses"] = task.Uses;
        if (task.With is { Count: > 0 }) map["with"] = BuildJsonMap(task.With);
        if (task.Expect is { Count: > 0 }) map["expect"] = BuildJsonMap(task.Expect);
        if (task.Artifacts is { IsEmpty: false })
        {
            map["artifacts"] = new Dictionary<string, object?>
            {
                ["files"] = task.Artifacts.Files
                    .Select(file => (object?)new Dictionary<string, object?> { ["path"] = file.Path })
                    .ToList(),
            };
        }
        if (task.SetVars is { Count: > 0 })
            map["setVars"] = task.SetVars.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
        if (task.Recovery is not null)
            map["recovery"] = BuildRecovery(task.Recovery);
        return map;
    }

    private static Dictionary<string, object?> BuildCheck(CheckDefinition check)
    {
        var map = new Dictionary<string, object?>
        {
            ["id"] = check.Id,
            ["title"] = check.Title ?? check.Id,
        };
        if (!string.IsNullOrEmpty(check.Uses)) map["uses"] = check.Uses;
        if (check.With is { Count: > 0 }) map["with"] = BuildJsonMap(check.With);
        return map;
    }

    private static Dictionary<string, object?> BuildRecovery(RecoveryDefinition recovery)
    {
        var map = new Dictionary<string, object?>
        {
            ["budget"] = recovery.Budget,
            ["handlers"] = recovery.Handlers.Select(BuildRecoveryHandler).ToList(),
        };
        return map;
    }

    private static Dictionary<string, object?> BuildRecoveryHandler(RecoveryHandlerDefinition handler)
    {
        var map = new Dictionary<string, object?>
        {
            ["tasks"] = handler.Tasks.Select(BuildTask).ToList(),
            ["retrySelf"] = handler.RetrySelf,
        };
        if (!string.IsNullOrEmpty(handler.When)) map["when"] = handler.When;
        return map;
    }

    private static Dictionary<string, object?> BuildApproval(ApprovalConfig approval)
    {
        var map = new Dictionary<string, object?>();
        if (approval.Feedback is { Tasks: { Count: > 0 } tasks })
        {
            map["feedback"] = new Dictionary<string, object?>
            {
                ["tasks"] = tasks.Select(BuildTask).ToList(),
            };
        }
        return map;
    }

    private static Dictionary<string, object?> BuildJsonMap(System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement?> entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map[entry.Key] = entry.Value is { } value
                ? JsonElementToObject(value)
                : null;
        }
        return map;
    }

    private static object? JsonElementToObject(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l)
            ? (object)l
            : element.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.Array => element.EnumerateArray()
            .Select(JsonElementToObject)
            .ToList(),
        System.Text.Json.JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => JsonElementToObject(property.Value)),
        _ => element.ToString(),
    };
}
