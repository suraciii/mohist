using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Slack.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private static void MapReportRoute(RouteGroupBuilder group)
    {
        // Reports go directly to the owning grain. Only an accepted durable
        // transition is a terminal acknowledgement; stale reports remain in
        // the Runner journal for retry.
        group.MapPost("/report", async (
            string runnerId,
            HttpRequest request,
            IGrainFactory grains,
            WorkflowReportService workflowReport,
            ManagerExecutionCapabilityIssuer managerCredentials,
            CancellationToken ct) =>
        {
            RunnerReportRequest? req;
            try
            {
                req = await request.ReadFromJsonAsync<RunnerReportRequest>(JSON.Options, ct);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("Invalid report body");
            }

            if (req is null)
                return ApiResults.BadRequest("request body is required");

            if (string.IsNullOrWhiteSpace(req.OwnerKind))
                return ApiResults.BadRequest("ownerKind is required");

            var ownerKind = req.OwnerKind.Trim().ToLowerInvariant();
            if (string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(req.AgentJobId))
                    return ApiResults.BadRequest("agentJobId is required when ownerKind is 'agent-job'");
                if (!string.IsNullOrWhiteSpace(req.WorkflowRunId))
                    return ApiResults.BadRequest("workflowRunId is not allowed when ownerKind is 'agent-job'");
            }
            else if (string.Equals(ownerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(req.WorkflowRunId))
                    return ApiResults.BadRequest("workflowRunId is required when ownerKind is 'workflow'");
                if (!string.IsNullOrWhiteSpace(req.AgentJobId))
                    return ApiResults.BadRequest("agentJobId is not allowed when ownerKind is 'workflow'");
            }
            else
            {
                return ApiResults.BadRequest($"ownerKind '{req.OwnerKind}' is not supported");
            }

            var result = new WorkResult(req.Status, req.Message, req.Output, req.ExitCode, req.ArtifactUploadIds, req.AddTasks, req.Error);

            if (string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            {
                var report = await grains.GetGrain<IAgentJobGrain>(req.AgentJobId ?? string.Empty)
                    .ReportResultAsync(runnerId, req.WorkId, result);
                var agentJobVerdict = report.Accepted ? "accepted" : "refused";
                managerCredentials.RevokeWork(req.AgentJobId ?? string.Empty, req.WorkId);
                return Results.Ok(new RunnerReportResponse(agentJobVerdict));
            }

            var (ack, workflowStatus) = await workflowReport.ReportAsync(
                runnerId,
                req.WorkflowRunId ?? string.Empty,
                req.WorkId,
                req.TaskRunId,
                result,
                ct,
                req.AgentSessionId,
                req.AgentTurnId,
                req.Runtime,
                req.RuntimeSessionId);
            var verdict = string.Equals(ack, ReportAck.Accepted.ToString().ToLowerInvariant(), StringComparison.Ordinal)
                ? "accepted"
                : "refused";
            return Results.Ok(new RunnerReportResponse(verdict));
        });
    }
}
