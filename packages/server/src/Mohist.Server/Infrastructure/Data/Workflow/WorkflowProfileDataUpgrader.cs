using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Workflow.Definition;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public static class WorkflowProfileDataUpgrader
{
    public static async Task UpgradeAsync(
        MohistDbContext db,
        CancellationToken cancellationToken = default,
        bool persistChanges = true)
    {
        var diagnostics = new List<string>();
        var projectRows = await db.ProjectWorkflowTemplates.ToListAsync(cancellationToken);
        var issueRows = await db.IssueWorkflowProfiles.ToListAsync(cancellationToken);

        foreach (var row in projectRows)
        {
            if (TryConvert(row.Template, row.TemplateId, row.TemplateId, out var profile, out var diagnostic) && profile is not null)
                row.Template = JSON.Serialize(profile);
            else if (diagnostic is not null)
                diagnostics.Add($"Project '{row.ProjectId}' Profile '{row.TemplateId}': {diagnostic}");
        }

        foreach (var row in issueRows)
        {
            var id = $"issue-custom:{row.ProjectId}#{row.IssueNumber}";
            if (TryConvert(row.Template, id, id, out var profile, out var diagnostic) && profile is not null)
                row.Template = JSON.Serialize(profile);
            else if (diagnostic is not null)
                diagnostics.Add($"Issue '{row.ProjectId}#{row.IssueNumber}' Profile '{id}': {diagnostic}");
        }

        if (diagnostics.Count > 0)
            throw new InvalidOperationException("Workflow Profile Definition migration failed:\n" + string.Join("\n", diagnostics));

        if (persistChanges && (projectRows.Count > 0 || issueRows.Count > 0))
            await db.SaveChangesAsync(cancellationToken);
    }

    private static bool TryConvert(
        string? json,
        string id,
        string name,
        out WorkflowProfile? profile,
        out string? diagnostic)
    {
        profile = null;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Profile payload must be an object");
            var variablePaths = new List<string>();
            var definitionElement = root.TryGetProperty("definition", out var storedDefinition)
                ? storedDefinition
                : root;
            if (definitionElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Profile definition must be an object");

            if (root.TryGetProperty("variables", out _)) variablePaths.Add("variables");
            if (root.TryGetProperty("defaults", out _)) variablePaths.Add("defaults");
            if (definitionElement.TryGetProperty("variables", out _)) variablePaths.Add("variables");
            if (definitionElement.TryGetProperty("stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var stage in stages.EnumerateArray())
                {
                    if (stage.ValueKind == JsonValueKind.Object && stage.TryGetProperty("variables", out _))
                        variablePaths.Add($"stages[{index}].variables");
                    index++;
                }
            }

            if (variablePaths.Count > 0)
            {
                diagnostic = $"Profile '{id}' contains Variables at: {string.Join(", ", variablePaths)}";
                return false;
            }

            var definitionNode = JsonNode.Parse(definitionElement.GetRawText())?.AsObject()
                ?? throw new JsonException("Profile definition must be an object");
            MapCheckNamesToIds(definitionNode);
            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(definitionNode.ToJsonString(JSON.Options), JSON.Options)
                ?? throw new JsonException("Definition payload is null");
            var description = root.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            var parsed = WorkflowDefinitionParser.Parse(SerializeDefinition(definition));
            if (!parsed.IsValid)
            {
                diagnostic = string.Join("; ", parsed.Errors.Select(error => $"{error.Path}: {error.Message}"));
                return false;
            }
            profile = new WorkflowProfile(id, name, description, parsed.Definition!);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            diagnostic = exception is JsonException jsonException && jsonException.Path is not null
                ? $"{jsonException.Path.TrimStart('$', '.')}: {exception.Message}"
                : exception.Message;
            return false;
        }
    }

    private static void MapCheckNamesToIds(JsonObject definition)
    {
        if (definition["stages"] is not JsonArray stages) return;
        foreach (var stage in stages.OfType<JsonObject>())
        {
            if (stage["checks"] is not JsonArray checks) continue;
            foreach (var check in checks.OfType<JsonObject>())
            {
                if (check["id"] is null && check.TryGetPropertyValue("name", out var name))
                {
                    check.Remove("name");
                    check["id"] = name;
                }
            }
        }
    }

    private static string SerializeDefinition(WorkflowDefinition definition) =>
        new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build()
            .Serialize(definition);
}
