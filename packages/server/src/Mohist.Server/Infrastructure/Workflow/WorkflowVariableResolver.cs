using System.Text.Json;
using Mohist.Server.Project.Queries;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Infrastructure.Workflow;

public sealed class WorkflowVariableResolver
{
    private readonly ProjectQueryService _projects;

    public WorkflowVariableResolver(ProjectQueryService projects)
    {
        _projects = projects;
    }

    public async Task<ResolvedWorkflowVariables> ResolveForDispatchAsync(
        string workflowRunId,
        WorkflowExecutionContext? context,
        WorkflowDispatchContext dispatch)
    {
        if (context is null)
            return new ResolvedWorkflowVariables(null);

        var projectId = context.String("project", "id");
        var project = string.IsNullOrWhiteSpace(projectId)
            ? null
            : await _projects.GetByIdAsync(projectId);

        var projectVariablesJson = project?.Variables.ToJson();
        return new ResolvedWorkflowVariables(context.ToDispatchJson(dispatch, projectVariablesJson));
    }
}

public sealed record ResolvedWorkflowVariables(string? Json)
{
    public JsonElement? NestedSection(string section, string property)
    {
        if (string.IsNullOrWhiteSpace(Json))
            return null;

        using var document = JsonDocument.Parse(Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(section, out var sectionValue)
            || sectionValue.ValueKind != JsonValueKind.Object
            || !sectionValue.TryGetProperty(property, out var propertyValue))
            return null;

        return propertyValue.Clone();
    }

    public string? String(string section, string property)
    {
        var value = NestedSection(section, property);
        if (!value.HasValue)
            return null;

        return value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            _ => value.Value.GetRawText(),
        };
    }
}
