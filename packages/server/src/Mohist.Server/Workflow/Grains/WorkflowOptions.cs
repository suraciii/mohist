namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Configuration knobs for workflow execution from the control-plane
/// perspective. Used by <see cref="RunnerGrain"/> to enforce the unified
/// work-completion timeout safety net.
/// </summary>
/// <remarks>
/// Bind from <c>Mohist:Workflow</c> in <c>~/.mohist/config.jsonc</c>.
/// </remarks>
public sealed class WorkflowOptions
{
    public const string SectionName = "Mohist:Workflow";

    public TimeSpan WorkCompletionTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
