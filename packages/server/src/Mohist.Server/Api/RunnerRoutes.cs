using System.Text.Json.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Queries;

namespace Mohist.Server.Api;

public static class RunnerRoutes
{
    public static WebApplication MapRunnerRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/{runnerId}");

        group.MapPost("/register", async (string runnerId, RunnerRegisterRequest req, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.RegisterAsync(new RunnerInfo(runnerId, req.Capabilities, req.Hostname ?? Environment.MachineName));
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
            return Results.Ok();
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

        return app;
    }
}

public record RunnerRegisterRequest(string[] Capabilities, string? Hostname = null);
public record RunnerReportRequest(string WorkId, string Status, string? Message = null, string? Output = null, int? ExitCode = null);
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
