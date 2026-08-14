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
                binding?.WorkflowProfileId,
                binding?.AgentAction,
                binding?.AgentRuntime);

            return ApiResults.Ok(detail);
        });

        return app;
    }
}
