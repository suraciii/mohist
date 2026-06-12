using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Api;

public static class RunnerRoutes
{
    public static WebApplication MapRunnerRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/{runnerId}");

        group.MapPost("/register", async (string runnerId, RunnerRegisterRequest req, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.RegisterAsync(new RunnerInfo(
                runnerId,
                req.Capabilities,
                req.Hostname ?? Environment.MachineName,
                req.ProjectId,
                req.CoderModels,
                MaxWorkflowSlots: RunnerCapacity.Normalize(req.MaxWorkflowSlots)));
            return Results.Ok();
        });

        group.MapPost("/unregister", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.UnregisterAsync();
            return Results.Ok();
        });

        group.MapPost("/heartbeat", async (string runnerId, HttpRequest request, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var req = request.ContentLength.GetValueOrDefault() > 0
                ? await JsonSerializer.DeserializeAsync<RunnerHeartbeatRequest>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;

            if (req is not null)
            {
                var info = new RunnerInfo(
                    runnerId,
                    req.Capabilities ?? [],
                    req.Hostname ?? Environment.MachineName,
                    req.ProjectId,
                    req.CoderModels,
                    MaxWorkflowSlots: RunnerCapacity.Normalize(req.MaxWorkflowSlots));
                await runner.HeartbeatRepairAsync(info);
            }
            else
            {
                await runner.HeartbeatAsync();
            }
            return Results.Ok();
        });

        group.MapPost("/poll", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var work = await runner.PollAsync();
            if (work is null) return Results.NoContent();

            return Results.Ok(new WorkDispatchResponse(
                work.WorkflowRunId,
                work.WorkId,
                work.Uses,
                work.With,
                work.Variables,
                work.WorkType,
                work.Stage,
                work.Title,
                work.Issue?.ProjectId,
                work.Issue?.IssueId,
                work.Issue?.IssueNumber));
        });

        group.MapPost("/report", async (string runnerId, RunnerReportRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.WorkflowRunId))
                return ApiResults.BadRequest("workflowRunId is required");

            var result = new WorkResult(req.Status, req.Message, req.Output, req.ExitCode);
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var report = await runner.ReportResultAsync(req.WorkflowRunId, req.WorkId, result);

            return Results.Ok(new RunnerReportResponse(report.WorkflowRunId, report.WorkflowStatus, report.Tracked, report.Reason));
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/ensure", async (
            string runnerId, string projectId, string workflowRunId, string sessionName,
            AgentSessionEnsureRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IAgentSessionGrain>(GrainKey.AgentSession(projectId, workflowRunId, sessionName));
            var session = await grain.EnsureAsync(new EnsureAgentSessionCommand(
                projectId, req.IssueNumber, workflowRunId, sessionName,
                runnerId, req.WorkId, req.WorkType, req.Stage, req.Title));
            return Results.Ok(session);
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/attach", async (
            string projectId, string workflowRunId, string sessionName,
            AgentSessionAttachRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IAgentSessionGrain>(GrainKey.AgentSession(projectId, workflowRunId, sessionName));
            return Results.Ok(await grain.AttachAgentAsync(new AttachAgentCommand(
                req.AgentSessionId, req.Model, req.WorkDir, req.ChangeDir, req.ProcessPid)));
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/session-events", async (
            string projectId, string workflowRunId, string sessionName,
            AgentSessionEventsRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IAgentSessionGrain>(GrainKey.AgentSession(projectId, workflowRunId, sessionName));
            var inputs = req.Events.Select(e => new AgentSessionEventInput(
                e.Type,
                e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            return Results.Ok(await grain.AppendSessionEventsAsync(new AppendAgentSessionEventsCommand(req.WorkId, req.WorkType, req.Stage, inputs)));
        });

        return app;
    }
}

public record RunnerRegisterRequest(string[] Capabilities, string? ProjectId = null, string? Hostname = null, string[]? CoderModels = null, int? MaxWorkflowSlots = null);
public record RunnerHeartbeatRequest(string[]? Capabilities = null, string? ProjectId = null, string? Hostname = null, string[]? CoderModels = null, int? MaxWorkflowSlots = null);
public record RunnerReportRequest(string WorkId, string Status, string WorkflowRunId, string? ProjectId = null, string? Message = null, string? Output = null, int? ExitCode = null);
public record RunnerReportResponse(string WorkflowRunId, string? WorkflowStatus, bool Tracked, string? Reason = null);
public record AgentSessionEnsureRequest(string? WorkId = null, string? WorkType = null, string? Stage = null, string? Title = null, int? IssueNumber = null);
public record AgentSessionAttachRequest(string AgentSessionId, string? Model = null, string? WorkDir = null, string? ChangeDir = null, int? ProcessPid = null);
public record AgentSessionEventsRequest(string? WorkId, string? WorkType, string? Stage, IReadOnlyList<AgentSessionEventRequest> Events);
public record AgentSessionEventRequest(string Type, System.Text.Json.JsonElement Payload);
public record WorkDispatchResponse(
    string WorkflowRunId,
    string WorkId,
    string? Uses,
    string? With,
    string? Variables,
    string WorkType,
    string? Stage,
    string? Title,
    string? ProjectId = null,
    string? IssueId = null,
    int? IssueNumber = null);
