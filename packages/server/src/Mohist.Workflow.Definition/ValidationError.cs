namespace Mohist.Workflow.Definition;

public enum ValidationSource
{
    Definition,
    Action,
}

public sealed record ValidationError(
    string Path,
    string Message,
    ValidationSource Source = ValidationSource.Definition)
{
    public override string ToString() => $"{Path}: {Message}";
}
