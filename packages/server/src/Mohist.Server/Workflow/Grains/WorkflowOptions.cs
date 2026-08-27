namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Configuration knobs for Workflow-owned mechanical execution.
/// </summary>
/// <remarks>
/// Bind from <c>Mohist:Workflow</c> in <c>~/.mohist/config.jsonc</c>.
/// </remarks>
public sealed class WorkflowOptions
{
    public const string SectionName = "Mohist:Workflow";

    /// <summary>
    /// How long ordinary workflow work may remain recoverably interrupted
    /// after its runner loses presence before the workflow fails closed.
    /// This must remain longer than the runner's two-minute presence timeout.
    /// </summary>
    public TimeSpan RunnerLossRecoveryTimeout { get; set; } = TimeSpan.FromMinutes(15);
}
