namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Configuration knobs for workflow execution from the control-plane
/// perspective. Work completion has no server-side wall clock, but an Agent
/// result that remains physically unconfirmed has a bounded settlement window.
/// </summary>
/// <remarks>
/// Bind from <c>Mohist:Workflow</c> in <c>~/.mohist/config.jsonc</c>.
/// </remarks>
public sealed class WorkflowOptions
{
    public const string SectionName = "Mohist:Workflow";

    public TimeSpan AgentResultSettlementTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Period of the finalizer reconcile reminder that resumes interrupted
    /// Agent invocation settlements from the recorded per-effect receipt
    /// flags (issue 559, design D7). Short by design — settlement must not
    /// depend solely on event-delivery retries.
    /// </summary>
    public TimeSpan AgentInvocationSettlementReconcileInterval { get; set; } = TimeSpan.FromSeconds(1);
}
