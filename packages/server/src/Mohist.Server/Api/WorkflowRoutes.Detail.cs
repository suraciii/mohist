using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Bare <c>GET /api/workflow-runs/{workflowRunId}</c> — the read model
/// surfaced to <c>mo workflow show &lt;runId&gt;</c> / <c>mo workflow
/// status &lt;runId&gt;</c>. Returns a
/// <see cref="WorkflowRunDetailDto"/> that composes the existing
/// <see cref="WorkflowStatusView"/> with an optional associated-issue
/// reference (number + title) reverse-resolved via
/// <see cref="IssueQuerier.GetIssueRefForWorkflowRunAsync"/>.
/// <para>
/// Composition (rather than nesting the issue ref inside the view)
/// preserves the invariant
/// (<c>tests/.../Workflow/Grain/StatusSpecs.cs:129</c>) that
/// <see cref="WorkflowStatusView"/> does not carry issue fields.
/// </para>
/// <para>
/// Read-only, no grain activation. The associated-issue lookup is a
/// single indexed query against <c>IssueRow.WorkflowRunId</c>; a
/// transiently-missing issue row renders <c>issueRef: null</c> rather
/// than failing the read.
/// </para>
/// </summary>
public static partial class WorkflowRoutes
{
    public static WebApplication MapWorkflowRunDetailRoute(this WebApplication app)
    {
        app.MapGet("/api/workflow-runs/{workflowRunId}", async (
            string workflowRunId,
            WorkflowQuerier workflowReader,
            IssueQuerier issueQuerier) =>
        {
            var status = await workflowReader.GetStatusAsync(workflowRunId);
            if (status is null) return ApiResults.NotFound($"Workflow run '{workflowRunId}' not found");

            var issueRef = await issueQuerier.GetIssueRefForWorkflowRunAsync(workflowRunId);
            var binding = await workflowReader.GetBindingAsync(workflowRunId);
            var detail = new WorkflowRunDetailDto(
                status,
                issueRef,
                binding?.WorkflowProfileId);

            return ApiResults.Ok(detail);
        });

        return app;
    }

    /// <summary>
    /// <c>GET /api/workflow-runs/{workflowRunId}/binding</c> — the on-demand
    /// actual-binding read (issue #1099), the resource behind
    /// <c>mo run view &lt;runId&gt; --json binding</c>. Returns the
    /// start-time facts the run itself retains and, when a snapshot was
    /// retained, the complete bound semantic definition. A historical run
    /// without a snapshot answers with its known identity/status and an
    /// explicit unavailability reason instead of falling back to the latest
    /// Profile. The read sits on the same authenticated surface as the
    /// detail route; it grants no new access path.
    /// </summary>
    public static WebApplication MapWorkflowRunBindingRoute(this WebApplication app)
    {
        app.MapGet("/api/workflow-runs/{workflowRunId}/binding", async (
            string workflowRunId,
            WorkflowQuerier workflowReader) =>
        {
            var binding = await workflowReader.GetBindingAsync(workflowRunId);
            return binding is null
                ? ApiResults.NotFound($"Workflow run '{workflowRunId}' not found")
                : ApiResults.Ok(binding);
        });

        return app;
    }
}
