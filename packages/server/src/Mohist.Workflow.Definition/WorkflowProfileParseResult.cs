namespace Mohist.Workflow.Definition;

public sealed record WorkflowProfileParseResult(
    WorkflowProfile? Profile,
    IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}
