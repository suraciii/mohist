namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Configuration knobs for workflow execution from the control-plane
/// perspective. Under the reconciliation model there is no server-side
/// work-completion wall clock — work liveness is the worker process's poll
/// report, and the only server-side timer is presence expiry. This options
/// type is retained as a binding anchor for <c>Mohist:Workflow</c>; the
/// former <c>WorkCompletionTimeout</c> knob has been removed.
/// </summary>
/// <remarks>
/// Bind from <c>Mohist:Workflow</c> in <c>~/.mohist/config.jsonc</c>.
/// </remarks>
public sealed class WorkflowOptions
{
    public const string SectionName = "Mohist:Workflow";
}
