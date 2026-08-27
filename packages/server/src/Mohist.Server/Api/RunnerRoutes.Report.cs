using System.Text.Json;
using Mohist.Server.Infrastructure.Data.AgentJobs;
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
    private static string VerdictValue(WorkReportVerdict verdict) => verdict switch
    {
        WorkReportVerdict.Accepted => "accepted",
        WorkReportVerdict.Refused => "refused",
        WorkReportVerdict.Outstanding => "outstanding",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

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

            if (string.IsNullOrWhiteSpace(req.WorkId))
                return ApiResults.BadRequest("workId is required");
            if (string.IsNullOrWhiteSpace(req.OwnerKind))
                return ApiResults.BadRequest("ownerKind is required");

            var ownerKind = req.OwnerKind.Trim().ToLowerInvariant();
            if (string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            {
                if (!WorkReportStatus.IsWork(req.Status))
                    return ApiResults.BadRequest("status is invalid");
                if (string.IsNullOrWhiteSpace(req.AgentJobId))
                    return ApiResults.BadRequest("agentJobId is required when ownerKind is 'agent-job'");
                if (!string.IsNullOrWhiteSpace(req.WorkflowRunId))
                    return ApiResults.BadRequest("workflowRunId is not allowed when ownerKind is 'agent-job'");
            }
            else if (string.Equals(ownerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal))
            {
                if (!WorkReportStatus.IsWorkflowEnvelope(req.Status))
                    return ApiResults.BadRequest("status is invalid");
                if (string.IsNullOrWhiteSpace(req.WorkflowRunId))
                    return ApiResults.BadRequest("workflowRunId is required when ownerKind is 'workflow'");
                if (!string.IsNullOrWhiteSpace(req.AgentJobId))
                    return ApiResults.BadRequest("agentJobId is not allowed when ownerKind is 'workflow'");
            }
            else
            {
                return ApiResults.BadRequest($"ownerKind '{req.OwnerKind}' is not supported");
            }

            var result = new WorkResult(
                req.Status,
                req.Message,
                req.Output,
                req.ExitCode,
                req.ArtifactUploadIds,
                req.AddTasks,
                req.Error,
                req.AgentSessionId,
                req.AgentTurnId,
                req.Runtime,
                req.RuntimeSessionId);

            if (string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            {
                AgentJobReportResult report;
                try
                {
                    report = await grains.GetGrain<IAgentJobGrain>(req.AgentJobId ?? string.Empty)
                        .ReportResultAsync(runnerId, req.WorkId, result);
                }
                catch (AgentJobLedgerConflictException)
                {
                    report = new AgentJobReportResult(WorkReportVerdict.Outstanding, "ledger-conflict");
                }
                if (report.Verdict is WorkReportVerdict.Accepted or WorkReportVerdict.Refused)
                    managerCredentials.RevokeWork(req.AgentJobId ?? string.Empty, req.WorkId);
                return Results.Ok(new RunnerReportResponse(VerdictValue(report.Verdict)));
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
            return Results.Ok(new RunnerReportResponse(ack));
        });
    }

}
