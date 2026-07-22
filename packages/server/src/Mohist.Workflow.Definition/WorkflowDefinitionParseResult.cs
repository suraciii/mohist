namespace Mohist.Workflow.Definition;

public sealed record WorkflowDefinitionParseResult(
    WorkflowDefinition? Definition,
    IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Definition is not null && Errors.Count == 0;

    public static WorkflowDefinitionParseResult Success(WorkflowDefinition definition) =>
        new(definition, Array.Empty<ValidationError>());
}
