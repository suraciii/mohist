using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services.Sessions;

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

        group.MapGet("/sessions/{projectId}/{workflowRunId}/{sessionName}", async (
            string projectId, string workflowRunId, string sessionName,
            AgentSessionResolver sessions,
            CancellationToken ct) =>
        {
            var labels = WorkflowAgentSessionMetadata.LookupLabels(projectId, workflowRunId, sessionName);
            var session = await sessions.GetByLabelsAsync(labels, ct);
            return session is null
                ? ApiResults.NotFound($"Session {sessionName} not found")
                : ApiResults.Ok(ToRunnerAgentSession(projectId, workflowRunId, sessionName, session));
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/open", async (
            string runnerId, string projectId, string workflowRunId, string sessionName,
            AgentSessionOpenRequest req, AgentSessionResolver sessions,
            CancellationToken ct) =>
        {
            var context = WorkflowSessionContext(projectId, workflowRunId, sessionName, req);
            var lookupLabels = WorkflowAgentSessionMetadata.LookupLabels(projectId, workflowRunId, sessionName);
            var sessionId = await sessions.ResolveByLabelsAsync(lookupLabels, ct) ?? sessions.NewSessionId();
            var grain = sessions.GetGrain(sessionId);
            var session = await grain.OpenAsync(new OpenAgentSessionCommand(
                runnerId,
                "opencode",
                Metadata: WorkflowAgentSessionMetadata.Metadata(context)));
            return Results.Ok(ToRunnerAgentSession(projectId, workflowRunId, sessionName, session));
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/attach", async (
            string projectId, string workflowRunId, string sessionName,
            AgentSessionAttachRequest req, AgentSessionResolver sessions,
            CancellationToken ct) =>
        {
            var sessionId = await sessions.ResolveByLabelsAsync(WorkflowAgentSessionMetadata.LookupLabels(projectId, workflowRunId, sessionName), ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {sessionName} not found");

            try
            {
                var session = await sessions.GetGrain(sessionId).AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                    req.AgentSessionId, req.Model, req.WorkDir, req.ChangeDir, req.ProcessPid));
                return Results.Ok(ToRunnerAgentSession(projectId, workflowRunId, sessionName, session));
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "agent_session_attach_conflict");
            }
        });

        group.MapPost("/sessions/{projectId}/{workflowRunId}/{sessionName}/runtime-events", async (
            string projectId, string workflowRunId, string sessionName,
            AgentSessionRuntimeEventsRequest req, AgentSessionResolver sessions,
            CancellationToken ct) =>
        {
            var sessionId = await sessions.ResolveByLabelsAsync(WorkflowAgentSessionMetadata.LookupLabels(projectId, workflowRunId, sessionName), ct);
            if (sessionId is null) return ApiResults.NotFound($"Session {sessionName} not found");

            var runtimeEvents = req.RuntimeEvents.Select(e => new AgentSessionRuntimeEventInput(
                e.Type,
                e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            return Results.Ok(await sessions.GetGrain(sessionId).AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(runtimeEvents)));
        });

        return app;
    }

    private static WorkflowAgentSessionContext WorkflowSessionContext(
        string projectId,
        string workflowRunId,
        string sessionName,
        AgentSessionOpenRequest req) =>
        new(
            projectId,
            workflowRunId,
            sessionName,
            req.IssueNumber is > 0 ? req.IssueNumber : null,
            req.WorkId,
            req.WorkType,
            req.Stage,
            req.Title);

    private static RunnerAgentSessionResponse ToRunnerAgentSession(string projectId, string workflowRunId, string sessionName, AgentSessionInfo session) =>
        new(
            new RunnerAgentSessionKey(projectId, workflowRunId, sessionName),
            session.AgentSessionId,
            session.Status,
            session.WorkDir,
            session.Model,
            session.ResolvedModel);
}

public record RunnerRegisterRequest(string[] Capabilities, string? ProjectId = null, string? Hostname = null, string[]? CoderModels = null, int? MaxWorkflowSlots = null);
public record RunnerHeartbeatRequest(string[]? Capabilities = null, string? ProjectId = null, string? Hostname = null, string[]? CoderModels = null, int? MaxWorkflowSlots = null);
public record RunnerReportRequest(string WorkId, string Status, string WorkflowRunId, string? ProjectId = null, string? Message = null, string? Output = null, int? ExitCode = null);
public record RunnerReportResponse(string WorkflowRunId, string? WorkflowStatus, bool Tracked, string? Reason = null);
public record RunnerAgentSessionKey(string ProjectId, string WorkflowRunId, string SessionName);
public record RunnerAgentSessionResponse(RunnerAgentSessionKey Key, [property: JsonPropertyName("acpSessionId")] string? AgentSessionId, string Status, string? WorkDir = null, string? Model = null, string? ResolvedModel = null);
public record AgentSessionOpenRequest(string? WorkId = null, string? WorkType = null, string? Stage = null, string? Title = null, int? IssueNumber = null);
public record AgentSessionAttachRequest(string AgentSessionId, string? Model = null, string? WorkDir = null, string? ChangeDir = null, int? ProcessPid = null);
public record AgentSessionRuntimeEventsRequest(string? WorkId, string? WorkType, string? Stage, IReadOnlyList<AgentSessionRuntimeEventRequest> RuntimeEvents);
public record AgentSessionRuntimeEventRequest(string Type, System.Text.Json.JsonElement Payload);
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
