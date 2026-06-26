using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Grains;
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
                MaxWorkflowSlots: RunnerCapacity.Normalize(req.MaxWorkflowSlots),
                BuildGitHash: NormalizeBuildGitHash(req.BuildGitHash),
                CoderModelVariants: NormalizeCoderModelVariants(req.CoderModelVariants)));
            return Results.Ok();
        });

        group.MapPost("/unregister", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.UnregisterAsync();
            return Results.Ok();
        });

        group.MapPost("/heartbeat", async (string runnerId, HttpRequest request, IGrainFactory grains, RunnerConnectionTracker connections) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var req = request.ContentLength.GetValueOrDefault() > 0
                ? await JsonSerializer.DeserializeAsync<RunnerHeartbeatRequest>(request.Body, JSON.Options)
                : null;

            if (req is not null)
            {
                var info = new RunnerInfo(
                    runnerId,
                    req.Capabilities ?? [],
                    req.Hostname ?? Environment.MachineName,
                    req.ProjectId,
                    req.CoderModels,
                    MaxWorkflowSlots: RunnerCapacity.Normalize(req.MaxWorkflowSlots),
                    BuildGitHash: NormalizeBuildGitHash(req.BuildGitHash),
                    CoderModelVariants: NormalizeCoderModelVariants(req.CoderModelVariants));
                await runner.HeartbeatRepairAsync(info);

                if (!string.IsNullOrWhiteSpace(req.ConnectionId))
                {
                    connections.Register(runnerId, req.ConnectionId);
                }
            }
            else
            {
                await runner.HeartbeatAsync();
            }
            return Results.Ok();
        });

        group.MapPatch("", async (string runnerId, RunnerSlotsPatchRequest req, IGrainFactory grains) =>
        {
            if (req is null || req.Slots <= 0)
                return ApiResults.BadRequest("slots must be a positive integer");

            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            try
            {
                await runner.UpdateAsync(req.Slots);
            }
            catch (ArgumentOutOfRangeException)
            {
                // The grain repeats the positive-integer invariant; reject with
                // the same 400 contract if a future caller bypasses the route guard.
                return ApiResults.BadRequest("slots must be a positive integer");
            }

            return ApiResults.Ok(new RunnerSlotsPatchResponse(runnerId, req.Slots));
        });

        group.MapPost("/poll", async (string runnerId, IGrainFactory grains, Microsoft.Extensions.Options.IOptions<CleanupPolicyOptions> cleanupPolicyOptions) =>
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
                work.Issue?.IssueNumber,
                work.Artifacts,
                work.SetVars,
                work.OwnerKind,
                work.AgentJobId,
                CleanupPolicy: ToCleanupPolicyDto(cleanupPolicyOptions.Value),
                Recovery: work.Recovery));
        });

        group.MapPost("/report", async (string runnerId, RunnerReportRequest req, IGrainFactory grains) =>
        {
            var ownerKind = string.IsNullOrWhiteSpace(req.OwnerKind)
                ? WorkDispatchOwnerKinds.Workflow
                : req.OwnerKind.Trim().ToLowerInvariant();
            if (string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(req.AgentJobId))
                    return ApiResults.BadRequest("agentJobId is required when ownerKind is 'agent-job'");
            }
            else if (string.Equals(ownerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(req.WorkflowRunId))
                    return ApiResults.BadRequest("workflowRunId is required when ownerKind is 'workflow'");
            }
            else
            {
                return ApiResults.BadRequest($"ownerKind '{req.OwnerKind}' is not supported");
            }

            var result = new WorkResult(req.Status, req.Message, req.Output, req.ExitCode, req.ArtifactUploadIds, req.AddTasks);
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            var report = string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
                ? await runner.ReportAgentJobResultAsync(req.AgentJobId ?? string.Empty, req.WorkId, result)
                : await runner.ReportWorkflowResultAsync(req.WorkflowRunId ?? string.Empty, req.WorkId, result);

            return Results.Ok(new RunnerReportResponse(
                report.WorkflowRunId,
                report.WorkflowStatus,
                report.Tracked,
                report.Reason,
                report.OwnerKind,
                report.OwnerId));
        });

        // Batch status query for the runner's convergence backstop. The
        // runner only asks about workflow runs it still tracks in its local
        // active workspace registry; the server returns the current lifecycle
        // status of every requested run id that exists, dropping unknown
        // ones. The server does not scan or enumerate runs the runner did
        // not request — that backstop is owned by the runner, not the
        // server.
        group.MapPost("/workflow-runs/status", async (
            string runnerId,
            RunnerWorkflowStatusRequest req,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (req is null)
                return ApiResults.BadRequest("request body is required");
            if (req.WorkflowRunIds is null || req.WorkflowRunIds.Length == 0)
                return ApiResults.BadRequest("workflowRunIds must contain at least one run id");

            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in req.WorkflowRunIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    unique.Add(id);
            }

            var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var workflowRunId in unique)
            {
                var workflow = grains.GetGrain<IWorkflowGrain>(workflowRunId);
                var status = await workflow.GetRunStatusAsync();
                if (!string.IsNullOrEmpty(status))
                    statuses[workflowRunId] = status;
            }

            return Results.Ok(new RunnerWorkflowStatusResponse(statuses));
        });

        group.MapGet("/sessions/{projectId}/{workflowRunId}/{sessionName}", async (
            string projectId, string workflowRunId, string sessionName,
            AgentSessionResolver sessions,
            CancellationToken ct) =>
        {
            var labels = WorkflowAgentSessionMetadata.LookupLabels(projectId, workflowRunId, sessionName);
            var session = await sessions.GetByLabelsAsync(labels, ct);
            if (session is null)
                return ApiResults.NotFound($"Session {sessionName} not found");

            return Results.Ok(ToRunnerAgentSession(projectId, workflowRunId, sessionName, session));
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

    private static string? NormalizeBuildGitHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static Dictionary<string, string[]>? NormalizeCoderModelVariants(Dictionary<string, string[]>? variants)
    {
        if (variants is null || variants.Count == 0)
            return null;

        var normalized = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in variants)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            var cleaned = (entry.Value ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (cleaned.Length == 0)
                continue;

            normalized[entry.Key.Trim()] = cleaned;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    /// <summary>
    /// Project the server's <see cref="CleanupPolicyOptions"/> into the
    /// wire DTO that ships with every poll. Every field is nullable; a
    /// fully-unconfigured policy is serialized as <c>{"retentionDays":null,...}</c>
    /// — or omitted entirely when no field is set so the runner can rely on
    /// "no fields configured ⇒ no eviction" without parsing nulls. The runner
    /// never sees a sentinel that distinguishes "disabled" from "missing"
    /// because the DTO uses null in both cases; that is the explicit
    /// unlimited/disabled contract.
    /// </summary>
    internal static CleanupPolicyDto ToCleanupPolicyDto(CleanupPolicyOptions options)
    {
        var retention = options.RetentionDays is > 0 ? options.RetentionDays : null;
        var budget = options.StorageBudgetBytes is > 0 ? options.StorageBudgetBytes : null;
        var watermark = options.StorageTargetWatermarkBytes is > 0 ? options.StorageTargetWatermarkBytes : null;
        return new CleanupPolicyDto(retention, budget, watermark);
    }
}

public record RunnerRegisterRequest(
    string[] Capabilities,
    string? ProjectId = null,
    string? Hostname = null,
    string[]? CoderModels = null,
    int? MaxWorkflowSlots = null,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null);
public record RunnerSlotsPatchRequest(int Slots);
public record RunnerSlotsPatchResponse(string RunnerId, int Slots);
public record RunnerHeartbeatRequest(
    string[]? Capabilities = null,
    string? ProjectId = null,
    string? Hostname = null,
    string[]? CoderModels = null,
    int? MaxWorkflowSlots = null,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null,
    string? ConnectionId = null);
public record RunnerReportRequest(
    string WorkId,
    string Status,
    string? WorkflowRunId = null,
    string? ProjectId = null,
    string? Message = null,
    string? Output = null,
    int? ExitCode = null,
    string[]? ArtifactUploadIds = null,
    string? OwnerKind = null,
    string? AgentJobId = null,
    List<RuntimeTaskInput>? AddTasks = null);
public record RunnerReportResponse(
    string WorkflowRunId,
    string? WorkflowStatus,
    bool Tracked,
    string? Reason = null,
    string? OwnerKind = null,
    string? OwnerId = null);
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
    int? IssueNumber = null,
    string? Artifacts = null,
    string? SetVars = null,
    string? OwnerKind = null,
    string? AgentJobId = null,
    CleanupPolicyDto? CleanupPolicy = null,
    string? Recovery = null);

/// <summary>
/// Wire shape for the workspace cleanup policy that the server hands the
/// runner on every poll. Each nullable field is an explicit
/// unlimited/disabled sentinel — the runner treats <c>null</c> as
/// "do not evict by this strategy". The server never scans runner
/// filesystems; this DTO only describes policy, never actions.
/// </summary>
public record CleanupPolicyDto(
    int? RetentionDays = null,
    long? StorageBudgetBytes = null,
    long? StorageTargetWatermarkBytes = null);

/// <summary>
/// Body for <c>POST /api/runner/{runnerId}/workflow-runs/status</c>. The
/// runner lists its still-active registry entries; the server answers
/// with the current lifecycle status of each requested workflow run.
/// </summary>
public record RunnerWorkflowStatusRequest(string[] WorkflowRunIds);

/// <summary>
/// Response body for the batch status endpoint. Only the requested run ids
/// are echoed back; unknown / untracked run ids are simply absent.
/// </summary>
public record RunnerWorkflowStatusResponse(Dictionary<string, string> Statuses);
