using System.Text.Json.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Queries;
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
            await runner.RegisterAsync(new RunnerInfo(runnerId, req.Capabilities, req.Hostname ?? Environment.MachineName, req.CoderModels));
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

        group.MapPost("/poll", async (string runnerId, IGrainFactory grains, AgentSessionService sessions) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var work = await runner.PollAsync();
            if (work is null) return Results.NoContent();

            var session = await sessions.CreateForDispatchAsync(runnerId, work);

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
                work.Issue?.IssueNumber,
                session));
        });

        group.MapPost("/report", async (string runnerId, RunnerReportRequest req, IGrainFactory grains, AgentSessionService sessions) =>
        {
            var result = new WorkDispatchResult(req.Status, req.Message, req.Output, req.ExitCode);
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var workflowRunId = await runner.ReportAsync(req.WorkId, result);
            if (workflowRunId is not null)
                await sessions.MarkWorkReportedAsync(workflowRunId, req.WorkId, result);

            string? workflowStatus = null;
            if (workflowRunId is not null)
            {
                var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
                workflowStatus = (await workflow.GetStatusAsync())?.Status;
            }

            return Results.Ok(new RunnerReportResponse(workflowRunId, workflowStatus));
        });

        group.MapPost("/sessions/{sessionId}/started", async (string sessionId, SessionStartedRequest req, AgentSessionService sessions) =>
        {
            var session = await sessions.MarkStartedAsync(sessionId, req);
            return session is null ? ApiResults.NotFound($"Session {sessionId} not found") : ApiResults.Ok(session);
        });

        group.MapPost("/sessions/{sessionId}/events", async (string sessionId, SessionTranscriptEntriesRequest req, AgentSessionService sessions) =>
        {
            var transcriptEntries = await sessions.AppendTranscriptEntriesAsync(sessionId, req.TranscriptEntries);
            return ApiResults.Ok(transcriptEntries);
        });

        group.MapPost("/sessions/{sessionId}/status", async (string sessionId, SessionStatusRequest req, AgentSessionService sessions) =>
        {
            var session = await sessions.MarkStatusAsync(sessionId, req);
            return session is null ? ApiResults.NotFound($"Session {sessionId} not found") : ApiResults.Ok(session);
        });

        group.MapPost("/sessions/{sessionId}/completed", async (string sessionId, SessionCompletedRequest req, AgentSessionService sessions) =>
        {
            var session = await sessions.MarkCompletedAsync(sessionId, req);
            return session is null ? ApiResults.NotFound($"Session {sessionId} not found") : ApiResults.Ok(session);
        });

        group.MapPost("/workflow-sessions/{workflowRunId}/{sessionName}/ensure", async (string runnerId, string workflowRunId, string sessionName, WorkflowSessionEnsureRequest req, IGrainFactory grains) =>
        {
            var key = WorkflowSessionGrainKeys.ForName(workflowRunId, sessionName);
            var grain = grains.GetGrain<IWorkflowSessionGrain>(key);
            var session = await grain.EnsureAsync(new EnsureWorkflowSessionCommand(workflowRunId, sessionName, runnerId, req.ProjectId, req.IssueNumber, req.WorkId, req.WorkType, req.Stage, req.Title));
            return Results.Ok(session);
        });

        group.MapPost("/workflow-sessions/{workflowRunId}/{sessionName}/attach", async (string workflowRunId, string sessionName, WorkflowSessionAttachRequest req, IGrainFactory grains) =>
        {
            var key = WorkflowSessionGrainKeys.ForName(workflowRunId, sessionName);
            var grain = grains.GetGrain<IWorkflowSessionGrain>(key);
            return Results.Ok(await grain.AttachAcpSessionAsync(new AttachAcpSessionCommand(req.AcpSessionId, req.WorkDir, req.Model, req.ProcessPid)));
        });

        group.MapPost("/workflow-sessions/{workflowRunId}/{sessionName}/events", async (string workflowRunId, string sessionName, WorkflowSessionEventsRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IWorkflowSessionGrain>(WorkflowSessionGrainKeys.ForName(workflowRunId, sessionName));
            var inputs = req.Events.Select(e => new WorkflowSessionEventInput(e.Type, e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            return Results.Ok(await grain.AppendEventsAsync(new AppendWorkflowSessionEventsCommand(req.WorkId, req.WorkType, req.Stage, inputs)));
        });

        group.MapPost("/workflow-sessions/{workflowRunId}/{sessionName}/status", async (string workflowRunId, string sessionName, WorkflowSessionStatusRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IWorkflowSessionGrain>(WorkflowSessionGrainKeys.ForName(workflowRunId, sessionName));
            return Results.Ok(await grain.MarkStatusAsync(new WorkflowSessionStatusCommand(req.Status, req.LastDataAt, req.FailureReason)));
        });

        group.MapPost("/workflow-sessions/{workflowRunId}/{sessionName}/complete", async (string workflowRunId, string sessionName, WorkflowSessionCompleteRequest req, IGrainFactory grains) =>
        {
            var grain = grains.GetGrain<IWorkflowSessionGrain>(WorkflowSessionGrainKeys.ForName(workflowRunId, sessionName));
            return Results.Ok(await grain.CompleteAsync(new CompleteWorkflowSessionCommand(req.Status, req.FailureReason, req.ExitCode)));
        });

        return app;
    }
}

public record RunnerRegisterRequest(string[] Capabilities, string? Hostname = null, string[]? CoderModels = null);
public record RunnerReportRequest(string WorkId, string Status, string? Message = null, string? Output = null, int? ExitCode = null);
public record RunnerReportResponse(string? WorkflowRunId, string? WorkflowStatus);
public record WorkflowSessionEnsureRequest(string WorkId, string WorkType, string? Stage = null, string? Title = null, string? ProjectId = null, int? IssueNumber = null);
public record WorkflowSessionAttachRequest(string AcpSessionId, string? WorkDir = null, string? Model = null, int? ProcessPid = null);
public record WorkflowSessionEventsRequest(string WorkId, string WorkType, string? Stage, IReadOnlyList<WorkflowSessionEventRequest> Events);
public record WorkflowSessionEventRequest(string Type, System.Text.Json.JsonElement Payload);
public record WorkflowSessionStatusRequest([property: JsonPropertyName("status")] string Status, DateTime? LastDataAt = null, string? FailureReason = null);
public record WorkflowSessionCompleteRequest([property: JsonPropertyName("status")] string Status, string? FailureReason = null, int? ExitCode = null);
public record SessionTranscriptEntriesRequest([property: JsonPropertyName("events")] IReadOnlyList<SessionTranscriptEntryRequest> TranscriptEntries);
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
    int? IssueNumber = null,
    AgentSessionDto? Session = null);
