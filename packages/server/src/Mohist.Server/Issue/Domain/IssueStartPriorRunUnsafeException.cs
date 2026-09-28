using Orleans;

namespace Mohist.Server.Issue.Domain;

/// <summary>Starting again would risk replaying an effect from a terminal WorkflowRun.</summary>
[GenerateSerializer]
public sealed class IssueStartPriorRunUnsafeException : InvalidOperationException
{
    public IssueStartPriorRunUnsafeException(string workflowRunId, string reason)
        : base($"Cannot start this Issue: prior WorkflowRun '{workflowRunId}' has an unsettled external-effect boundary ({reason}). Inspect that run and reconcile the effect before starting again.")
    {
        WorkflowRunId = workflowRunId;
        Code = "prior_run_external_effect_unknown";
        NextStep = $"Inspect WorkflowRun {workflowRunId}; reconcile the external effect and retry only after its outcome is confirmed. No new WorkflowRun, Workspace, or AgentJob was created.";
    }

    [Id(0)]
    public string WorkflowRunId { get; }
    [Id(1)]
    public string Code { get; }
    [Id(2)]
    public string NextStep { get; }
}
