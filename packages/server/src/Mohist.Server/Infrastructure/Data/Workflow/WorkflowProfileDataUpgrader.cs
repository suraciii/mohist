using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public static class WorkflowProfileDataUpgrader
{
    public static async Task UpgradeAsync(MohistDbContext db, CancellationToken cancellationToken = default)
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
            throw new InvalidOperationException("Workflow Profile migration requires Variables relocation:\n" + string.Join("\n", diagnostics));

        if (projectRows.Count > 0 || issueRows.Count > 0)
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
            if (root.TryGetProperty("definition", out _)) return true;

            var variablePaths = new List<string>();
            if (root.TryGetProperty("variables", out _)) variablePaths.Add("variables");
            if (root.TryGetProperty("defaults", out _)) variablePaths.Add("defaults");
            if (root.TryGetProperty("stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
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

            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, JSON.Options)
                ?? throw new JsonException("Definition payload is null");
            var description = root.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            profile = new WorkflowProfile(id, name, description, definition);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            diagnostic = exception.Message;
            return false;
        }
    }
}
