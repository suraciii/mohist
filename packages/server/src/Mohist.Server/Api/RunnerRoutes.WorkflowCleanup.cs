using Microsoft.AspNetCore.Builder;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapWorkflowCleanupRoute(RouteGroupBuilder group)
    {
        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/cleanup-turn", async (
            string runnerId, string projectId, string workflowRunId, string sessionName,
            WorkflowAgentSessionCleanupTurnRequest req, AgentSessionResolver sessions,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.CleanupOperationId)
                || string.IsNullOrWhiteSpace(req.Prompt)
                || string.IsNullOrWhiteSpace(req.TaskRunId)
                || string.IsNullOrWhiteSpace(req.WorkId)
                || string.IsNullOrWhiteSpace(req.AgentSessionId)
                || string.IsNullOrWhiteSpace(req.Runtime)
                || string.IsNullOrWhiteSpace(req.RuntimeSessionId))
            {
                return ApiResults.BadRequest("cleanup turn requires its complete execution identity", "workflow_cleanup_identity_required");
            }

            var sessionId = await sessions.ResolveByLabelsAsync(
                WorkflowAgentSessionMetadata.LookupLabels(projectId, workflowRunId, sessionName),
                ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {sessionName} not found");
            if (!string.Equals(sessionId, req.AgentSessionId, StringComparison.Ordinal))
            {
                return ApiResults.Conflict(
                    "Workflow AgentSession changed before cleanup delivery",
                    "workflow_agent_session_changed");
            }

            var grain = sessions.GetGrain(sessionId);
            var existing = await grain.GetAsync();
            if (existing is null || !string.Equals(existing.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.NotFound($"Session {sessionName} not found");

            try
            {
                var receipt = await grain.AcceptWorkflowCleanupAsync(
                    new AcceptWorkflowAgentSessionCleanupCommand(
                        req.CleanupOperationId,
                        req.Prompt,
                        workflowRunId,
                        req.TaskRunId,
                        req.WorkId,
                        runnerId,
                        req.AgentSessionId,
                        req.Runtime,
                        req.RuntimeSessionId));
                return Results.Ok(new WorkflowAgentSessionCleanupTurnResponse(
                    receipt.CleanupOperationId,
                    receipt.InputDeliveryId,
                    receipt.AgentTurnId,
                    receipt.AgentSessionId));
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "workflow_cleanup_binding_rejected");
            }
        });
    }
}

public record WorkflowAgentSessionCleanupTurnRequest(
    string CleanupOperationId,
    string Prompt,
    string TaskRunId,
    string WorkId,
    string AgentSessionId,
    string Runtime,
    string RuntimeSessionId);

public record WorkflowAgentSessionCleanupTurnResponse(
    string CleanupOperationId,
    string InputDeliveryId,
    string AgentTurnId,
    string AgentSessionId);
