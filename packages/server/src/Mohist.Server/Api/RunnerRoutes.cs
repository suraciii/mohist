using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions;
using Mohist.Server.Variables.Grains;

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

            var scope = grains.GetGrain<IVariableScopeGrain>(work.WorkflowRunId);
            var variables = await scope.SnapshotAsync(new VariableSnapshotRequest(
                work.WorkflowRunId,
                work.WorkId,
                work.WorkType,
                work.Stage,
                work.Title));

            return Results.Ok(new WorkDispatchResponse(
                work.WorkflowRunId,
                work.WorkId,
                work.Uses,
                work.With,
                variables,
                work.WorkType,
                work.Stage,
                work.Title,
                work.Issue?.ProjectId,
                work.Issue?.IssueId,
                work.Issue?.IssueNumber,
                session));
        });

        group.MapPost("/report", async (string runnerId, RunnerReportRequest req, IGrainFactory grains) =>
        {
            var result = new WorkDispatchResult(req.Status, req.Message, req.Output, req.ExitCode);
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.ReportAsync(req.WorkId, result);
            return Results.Ok();
        });

        group.MapPost("/sessions/{sessionId}/started", async (string sessionId, SessionStartedRequest req, AgentSessionService sessions) =>
        {
            var session = await sessions.MarkStartedAsync(sessionId, req);
            return session is null ? ApiResults.NotFound($"Session {sessionId} not found") : ApiResults.Ok(session);
        });

        group.MapPost("/sessions/{sessionId}/events", async (string sessionId, SessionEventsRequest req, AgentSessionService sessions) =>
        {
            var events = await sessions.AppendEventsAsync(sessionId, req.Events);
            return ApiResults.Ok(events);
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
public record SessionEventsRequest(IReadOnlyList<SessionEventRequest> Events);
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
