namespace Mohist.Workflow.Definition;

public static class WorkflowDefinitionValidator
{
    public static IReadOnlyList<ValidationError> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<ValidationError>();
        if (definition is null)
        {
            errors.Add(new ValidationError("", "definition is required"));
            return Sort(errors);
        }

        WorkflowDefinitionRules.Apply(definition, errors);
        return Sort(errors);
    }

    internal static IReadOnlyList<ValidationError> Sort(List<ValidationError> errors) =>
        errors
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Message, StringComparer.Ordinal)
            .ToArray();
}
