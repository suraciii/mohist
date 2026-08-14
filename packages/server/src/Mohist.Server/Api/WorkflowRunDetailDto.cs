using System.Text.Json.Serialization;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Read model returned by <c>GET /api/workflow-runs/{workflowRunId}</c>.
/// <para>
/// Composes the existing <see cref="WorkflowStatusView"/> with an optional
/// reference to the issue associated with the run. The issue ref is
/// reverse-resolved by
/// <see cref="Mohist.Server.Issue.Services.IssueQuerier.GetIssueRefForWorkflowRunAsync"/>;
/// <c>IssueRef</c> is <c>null</c> when no issue row is bound —
/// the run identity and status remain authoritative, so the endpoint
/// still returns the full status when the issue row is transiently
/// missing.
/// </para>
/// <para>
/// Composition (rather than extending <see cref="WorkflowStatusView"/>)
/// preserves the invariant that the view does not carry issue fields
/// (asserted by <c>tests/.../Workflow/Grain/StatusSpecs.cs:129</c>). New
/// consumers that only want status data should keep reading the view
/// directly; <see cref="WorkflowRunDetailDto"/> is the response shape
/// for the new <c>show</c>/<c>status</c> HTTP endpoint and does not
/// pollute the lower-level view contract.
/// </para>
/// </summary>
public sealed record WorkflowRunDetailDto(
    WorkflowStatusView Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    WorkflowRunIssueRef? IssueRef,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? WorkflowProfileId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? AgentAction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? AgentRuntime);
