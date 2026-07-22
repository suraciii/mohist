using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

internal static class WorkflowProfilePersistence
{
    public static string Serialize(WorkflowProfile profile) =>
        JsonSerializer.Serialize(profile, JSON.Options);

    public static WorkflowProfile Deserialize(string json)
    {
        WorkflowProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<WorkflowProfile>(json, JSON.Options)
                ?? throw new JsonException("stored Profile must be a JSON object");
        }
        catch (JsonException exception)
        {
            var path = exception.Path?.TrimStart('$', '.') ?? "";
            throw new WorkflowDefinitionValidationException(
                [new ValidationError(path, "stored Profile is not valid JSON for a Workflow Profile")]);
        }
        var errors = WorkflowDefinitionValidator.Validate(profile.Definition);
        if (errors.Count > 0)
            throw new WorkflowDefinitionValidationException(errors);
        return profile;
    }
}
