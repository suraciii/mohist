using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Workflow.Grains;

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
                RunnerCapacity.Normalize(req.MaxWorkflowSlots)));
            return Results.Ok();
        });

        group.MapPost("/unregister", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.UnregisterAsync();
            return Results.Ok();
        });

        group.MapPost("/heartbeat", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.HeartbeatAsync();
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
            var result = new WorkDispatchResult(req.Status, req.Message, req.Output, req.ExitCode);
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var workflowRunId = await runner.ReportAsync(req.WorkId, result);

            string? workflowStatus = null;
            if (workflowRunId is not null)
            {
                var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
                workflowStatus = await workflow.GetRunStatusAsync();
            }

            return Results.Ok(new RunnerReportResponse(workflowRunId, workflowStatus));
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/ensure", async (
            string runnerId, string projectId, string workflowRunId, string sessionName,
            WorkflowAgentSessionEnsureRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(projectId, workflowRunId, sessionName));
            var session = await grain.EnsureAsync(new EnsureWorkflowAgentSessionCommand(
                projectId, req.IssueNumber, workflowRunId, sessionName,
                runnerId, req.WorkId, req.WorkType, req.Stage, req.Title));
            return Results.Ok(session);
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/attach", async (
            string projectId, string workflowRunId, string sessionName,
            WorkflowAgentSessionAttachRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(projectId, workflowRunId, sessionName));
            return Results.Ok(await grain.AttachAgentAsync(new AttachAgentCommand(
                req.AgentSessionId, req.Model, req.WorkDir, req.ChangeDir, req.ProcessPid)));
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/events", async (
            string projectId, string workflowRunId, string sessionName,
            WorkflowAgentSessionEventsRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IWorkflowAgentSessionGrain>(GrainKey.WorkflowAgentSession(projectId, workflowRunId, sessionName));
            var inputs = req.Events.Select(e => new WorkflowAgentSessionEventInput(
                e.Type,
                e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            return Results.Ok(await grain.AppendEventsAsync(new AppendWorkflowAgentSessionEventsCommand(req.WorkId, req.WorkType, req.Stage, inputs)));
        });

        return app;
    }
}

public record RunnerRegisterRequest(string[] Capabilities, string? ProjectId = null, string? Hostname = null, string[]? CoderModels = null, int? MaxWorkflowSlots = null);
public record RunnerReportRequest(string WorkId, string Status, string? ProjectId = null, string? Message = null, string? Output = null, int? ExitCode = null);
public record RunnerReportResponse(string? WorkflowRunId, string? WorkflowStatus);
public record WorkflowAgentSessionEnsureRequest(string? WorkId = null, string? WorkType = null, string? Stage = null, string? Title = null, int? IssueNumber = null);
public record WorkflowAgentSessionAttachRequest(string AgentSessionId, string? Model = null, string? WorkDir = null, string? ChangeDir = null, int? ProcessPid = null);
public record WorkflowAgentSessionEventsRequest(string? WorkId, string? WorkType, string? Stage, IReadOnlyList<WorkflowAgentSessionEventRequest> Events);
public record WorkflowAgentSessionEventRequest(string Type, System.Text.Json.JsonElement Payload);
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
