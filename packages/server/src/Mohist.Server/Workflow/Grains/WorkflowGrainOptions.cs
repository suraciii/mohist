namespace Mohist.Server.Workflow.Grains;

public sealed class WorkflowGrainOptions
{
    public const string SectionName = "Mohist:Workflow";

    public TimeSpan WorkCompletionTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
