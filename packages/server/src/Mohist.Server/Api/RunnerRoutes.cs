using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Issue.Services;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
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

        group.MapPost("/poll", async (
            string runnerId,
            HttpRequest request,
            IGrainFactory grains,
            Mohist.Server.Runner.Services.DispatchService dispatch,
            IssueQuerier issues,
            CancellationToken ct) =>
        {
            RunnerPollRequest req = new([], []);
            if (request.ContentLength is > 0)
            {
                try
                {
                    req = await request.ReadFromJsonAsync<RunnerPollRequest>(cancellationToken: ct)
                        ?? new RunnerPollRequest([], []);
                }
                catch
                {
                    // Malformed body → treat as empty report (old/buggy client).
                    req = new RunnerPollRequest([], []);
                }
            }

            var response = await dispatch.PollAsync(runnerId, req, ct);
            if (response.Dispatches.Count == 0) return Results.NoContent();

            var dispatches = await Task.WhenAll(response.Dispatches.Select(work =>
                ToWorkDispatchResponseAsync(work, issues.GetParentIssueContextAsync)));
            return Results.Ok(new RunnerPollResponseDto(dispatches.ToList()));
        });

        // Dedicated runner config channel. Separate from /poll so runner-side
        // configuration (e.g. cleanupPolicy) is reachable even when the
        // system is idle and /poll returns 204 No Content. Plain periodic
        // GET — no ETag, no version negotiation, no request body. The
        // cleanupPolicy is a pure projection of the server's bound
        // CleanupPolicyOptions through ToCleanupPolicyDto; the server
        // remains the single source of truth (the runner never reads
        // config.jsonc directly).
        //
        // Serialization uses a local JsonSerializerOptions that overrides
        // the global WhenWritingNull policy so that unconfigured
        // cleanupPolicy fields are emitted as explicit `null` rather than
        // omitted. The "null means unlimited / disabled" contract is only
        // meaningful end-to-end if the wire shape always carries every
        // field — the runner's CleanupPolicy TS type tolerates either
        // present-null or absent, but the spec (issue-359
        // runner-config-endpoint) requires the present-null form so the
        // response is self-describing. The override is local to this
        // handler; /poll no longer carries CleanupPolicy (T-002 removed the
        // field from WorkDispatchResponse atomically with the runner
        // switch to /config).
        // Per-request re-bind from the currently-loaded IConfiguration.
        // IOptions<T> would snapshot once at startup; IOptionsSnapshot<T>
        // is request-scoped (matches the minimal-API handler lifetime),
        // rebuilds every request through OptionsFactory<T>, and honors
        // every registered IConfigureOptions<T> in registration order.
        // Combined with the T-001 native-AddJsonFile wiring, a reload
        // of config.jsonc reaches the next /config call without a
        // server restart. No singleton consumes CleanupPolicyOptions
        // today, so IOptionsMonitor is unnecessary machinery.
        group.MapGet("/config", (Microsoft.Extensions.Options.IOptionsSnapshot<CleanupPolicyOptions> cleanupPolicyOptions) =>
        {
            return Results.Json(
                new RunnerConfigResponse(ToCleanupPolicyDto(cleanupPolicyOptions.Value)),
                RunnerConfigJsonOptions);
        });

        // Report direct to the owning grain. Agent-job reports still flow
        // through the runner grain (its ledger tracks the push-dispatched
        // work and the closeout path). Workflow reports no longer touch the
        // runner grain — translation is a stateless service, the workflow
        // grain is the idempotent arbiter, and the runner retires the work
        // from awaitingAck on Accepted or Stale (both are acks).
        group.MapPost("/report", async (
            string runnerId,
            RunnerReportRequest req,
            IGrainFactory grains,
            Mohist.Server.Runner.Services.WorkflowReportService workflowReport,
            CancellationToken ct) =>
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

            var result = new WorkResult(req.Status, req.Message, req.Output, req.ExitCode, req.ArtifactUploadIds, req.AddTasks, req.Error);

            // Agent-job: route through the runner grain (push-model ledger).
            if (string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            {
                var runner = grains.GetGrain<IRunnerGrain>(runnerId);
                var report = await runner.ReportAgentJobResultAsync(req.AgentJobId ?? string.Empty, req.WorkId, result);
                return Results.Ok(new RunnerReportResponse(
                    report.WorkflowRunId, report.WorkflowStatus, report.Tracked,
                    report.Reason, report.OwnerKind, report.OwnerId));
            }

            // Workflow: report direct to the owning grain via the stateless
            // report service (the runner grain no longer relays workflow
            // reports). Accepted and Stale are both acks.
            var (ack, workflowStatus) = await workflowReport.ReportAsync(
                runnerId, req.WorkflowRunId ?? string.Empty, req.WorkId, result, ct);
            var tracked = ack != "missing-workflow";
            return Results.Ok(new RunnerReportResponse(
                req.WorkflowRunId ?? string.Empty, workflowStatus, tracked, ack, ownerKind, req.WorkflowRunId ?? string.Empty));
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
                WorkDir: req.WorkDir,
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
                    req.RuntimeSessionId, req.Model, req.WorkDir, req.ChangeDir, req.ProcessPid, Runtime: "opencode"));
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
            if (string.IsNullOrWhiteSpace(req.RuntimeSessionId))
                return ApiResults.BadRequest("runtimeSessionId is required", "runtime_session_id_required");

            var runtimeEvents = req.RuntimeEvents.Select(e => new AgentSessionRuntimeEventInput(
                e.Type,
                e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            return Results.Ok(await sessions.GetGrain(sessionId).AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(runtimeEvents, req.RuntimeSessionId)));
        });

        // Generic (non-workflow) AgentSession routes — used by the runner
        // when it executes an agent-job dispatch whose launch minted an
        // AgentSession id (issue-129 T-002/T-003). Identifies a session by
        // (projectId, sessionId) without a workflowRunId/sessionName pair.
        group.MapGet("/agent-sessions/{projectId}/{sessionId}", async (
            string projectId, string sessionId,
            AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery,
            CancellationToken ct) =>
        {
            var session = await sessions.GetGrain(sessionId).GetAsync();
            if (session is null) return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (!await IsGenericAgentSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            return Results.Ok(ToRunnerGenericAgentSession(session));
        });

        group.MapPost("/agent-sessions/{projectId}/{sessionId}/open", async (
            string runnerId, string projectId, string sessionId,
            GenericAgentSessionOpenRequest? req, AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery,
            CancellationToken ct) =>
        {
            req ??= new GenericAgentSessionOpenRequest();
            var grain = sessions.GetGrain(sessionId);
            var existing = await grain.GetAsync();
            if (existing is null) return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (!await IsGenericAgentSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");
            // The session was minted up front by the launch endpoint
            // (T-003) carrying source-kind=agent-launch + agent id/name
            // labels. The runner's open call only contributes annotations
            // (workId/workType/stage/title/issueNumber) for traceability
            // — labels are intentionally left untouched so the launch
            // identity (projectId, agentId, agentName, source-kind) is
            // preserved by AgentSessionMetadata.Merge.
            var session = await grain.OpenAsync(new OpenAgentSessionCommand(
                runnerId,
                "opencode",
                WorkDir: req.WorkDir,
                Metadata: BuildGenericAgentSessionMetadata(req)));
            return Results.Ok(ToRunnerGenericAgentSession(session));
        });

        group.MapPost("/agent-sessions/{projectId}/{sessionId}/attach", async (
            string runnerId, string projectId, string sessionId,
            AgentSessionAttachRequest req, AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery, IGrainFactory grains,
            CancellationToken ct) =>
        {
            var grain = sessions.GetGrain(sessionId);
            var existing = await grain.GetAsync();
            if (existing is null) return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (!await IsGenericAgentSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            try
            {
                var session = await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                    req.RuntimeSessionId, req.Model, req.WorkDir, req.ChangeDir, req.ProcessPid, Runtime: "opencode"));
                if (!string.IsNullOrWhiteSpace(req.AgentJobId)
                    && !string.IsNullOrWhiteSpace(req.WorkId))
                {
                    await grains.GetGrain<IAgentJobGrain>(req.AgentJobId)
                        .RecordRuntimeSessionBindingAsync(runnerId, req.WorkId, sessionId, req.RuntimeSessionId);
                }
                return Results.Ok(ToRunnerGenericAgentSession(session));
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "agent_session_attach_conflict");
            }
        });

        group.MapPost("/agent-sessions/{projectId}/{sessionId}/runtime-events", async (
            string projectId, string sessionId,
            AgentSessionRuntimeEventsRequest req, AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery,
            CancellationToken ct) =>
        {
            var grain = sessions.GetGrain(sessionId);
            var existing = await grain.GetAsync();
            if (existing is null) return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (!await IsGenericAgentSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (string.IsNullOrWhiteSpace(req.RuntimeSessionId))
                return ApiResults.BadRequest("runtimeSessionId is required", "runtime_session_id_required");

            var runtimeEvents = req.RuntimeEvents.Select(e => new AgentSessionRuntimeEventInput(
                e.Type,
                e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            return Results.Ok(await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(runtimeEvents, req.RuntimeSessionId)));
        });

        return app;
    }

    internal static async Task<WorkDispatchResponse> ToWorkDispatchResponseAsync(
        WorkDispatch work,
        Func<string, int, Task<ParentIssueContext?>> resolveParentIssueContext)
    {
        ParentIssueContextResponse? parentIssueContext = null;
        var projectId = work.Issue?.ProjectId ?? work.ProjectId;
        var issueNumber = work.Issue?.IssueNumber;
        if (string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal)
            && string.Equals(work.WorkType, WorkItemTypes.Task, StringComparison.Ordinal)
            && string.Equals(work.Stage, "plan", StringComparison.Ordinal)
            && string.Equals(work.Uses, "mohist/opencode", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(projectId)
            && issueNumber is > 0)
        {
            var resolved = await resolveParentIssueContext(projectId, issueNumber.Value);
            if (resolved is not null)
                parentIssueContext = new ParentIssueContextResponse(resolved.Title, resolved.Body);
        }

        return new WorkDispatchResponse(
            work.WorkflowRunId,
            work.WorkId,
            work.Uses,
            work.With,
            work.Variables,
            work.WorkType,
            work.Stage,
            work.Title,
            projectId,
            issueNumber,
            work.EpicNumber,
            work.Artifacts,
            work.SetVars,
            work.OwnerKind,
            work.AgentJobId,
            AgentSessionId: work.AgentSessionId,
            Recovery: work.Recovery,
            RecoveryRemaining: work.RecoveryRemaining,
            Expect: work.Expect,
            ParentIssueContext: parentIssueContext);
    }

    private static RunnerGenericAgentSessionResponse ToRunnerGenericAgentSession(AgentSessionInfo session) =>
        new(
            session.AgentSessionId,
            session.Status,
            session.WorkDir,
            session.Model,
            session.ResolvedModel,
            session.Runtime);

    private static async Task<bool> IsGenericAgentSessionInProjectAsync(
        AgentSessionQuery sessionQuery,
        string projectId,
        string sessionId,
        CancellationToken ct)
    {
        var records = await sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null) return false;
        return string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal)
            && string.Equals(record.Label(AgentSessionQueryMetadataKeys.SourceKind), "agent-launch", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the annotations-only metadata that the runner contributes on
    /// open for a generic AgentSession. Labels are intentionally left null
    /// so the launch-time labels (source-kind=agent-launch, agent-id,
    /// agent-name, project-id) are preserved by
    /// <see cref="AgentSessionMetadata.Merge"/>.
    /// </summary>
    private static AgentSessionMetadata BuildGenericAgentSessionMetadata(GenericAgentSessionOpenRequest req)
    {
        IReadOnlyDictionary<string, string>? annotations = null;
        if (!string.IsNullOrWhiteSpace(req.Title) || req.IssueNumber is not null)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(req.Title))
                map[AgentSessionQueryMetadataKeys.Title] = req.Title!;
            if (req.IssueNumber is not null)
                map[AgentSessionQueryMetadataKeys.IssueNumber] = req.IssueNumber.Value.ToString();
            annotations = map;
        }
        return new AgentSessionMetadata(null, annotations);
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
            req.Title,
            EpicNumber: req.EpicNumber is > 0 ? req.EpicNumber : null);

    private static RunnerAgentSessionResponse ToRunnerAgentSession(string projectId, string workflowRunId, string sessionName, AgentSessionInfo session) =>
        new(
            new RunnerAgentSessionKey(projectId, workflowRunId, sessionName),
            session.AgentSessionId,
            session.Status,
            session.WorkDir,
            session.Model,
            session.ResolvedModel,
            session.Runtime);

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
    /// wire DTO returned by <c>GET /api/runner/{runnerId}/config</c>.
    /// Every field is nullable; a fully-unconfigured policy is
    /// serialized as <c>{"retentionDays":null,...}</c> so the runner
    /// can rely on "no fields configured ⇒ no eviction" without
    /// parsing nulls. The runner never sees a sentinel that
    /// distinguishes "disabled" from "missing" because the DTO uses
    /// null in both cases; that is the explicit unlimited/disabled
    /// contract.
    /// </summary>
    internal static CleanupPolicyDto ToCleanupPolicyDto(CleanupPolicyOptions options)
    {
        var retention = options.RetentionDays is > 0 ? options.RetentionDays : null;
        var budget = options.StorageBudgetBytes is > 0 ? options.StorageBudgetBytes : null;
        var watermark = options.StorageTargetWatermarkBytes is > 0 ? options.StorageTargetWatermarkBytes : null;
        return new CleanupPolicyDto(retention, budget, watermark);
    }

    /// <summary>
    /// Per-endpoint JSON options for the runner config channel. Mirrors
    /// <see cref="Mohist.Server.Infrastructure.JSON.Options"/> but flips
    /// <c>DefaultIgnoreCondition</c> to <c>Never</c> so that
    /// <c>cleanupPolicy</c> fields are always emitted, even when null.
    /// The runner's <c>CleanupPolicy</c> TS type tolerates either
    /// present-null or absent, but the spec (issue-359
    /// runner-config-endpoint) requires the present-null form so the
    /// response is self-describing: "null means unlimited / disabled"
    /// reads the same on the wire as it does in the bound options. The
    /// override is scoped to <c>GET /api/runner/{id}/config</c> only.
    /// </summary>
    internal static readonly System.Text.Json.JsonSerializerOptions RunnerConfigJsonOptions = new(JSON.Options)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}

public record RunnerRegisterRequest(
    string[] Capabilities,
    string? ProjectId = null,
    string? Hostname = null,
    string[]? CoderModels = null,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null);
public record RunnerSlotsPatchRequest(int Slots);
public record RunnerSlotsPatchResponse(string RunnerId, int Slots);
public record RunnerHeartbeatRequest(
    string[]? Capabilities = null,
    string? ProjectId = null,
    string? Hostname = null,
    string[]? CoderModels = null,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null,
    string? ConnectionId = null);
public record RunnerReportRequest(
    string WorkId,
    string Status,
    string? WorkflowRunId = null,
    string? ProjectId = null,
    string? Message = null,
    System.Text.Json.JsonElement? Output = null,
    int? ExitCode = null,
    string[]? ArtifactUploadIds = null,
    string? OwnerKind = null,
    string? AgentJobId = null,
    List<RuntimeTaskInput>? AddTasks = null,
    ExecutionError? Error = null);
public record RunnerReportResponse(
    string WorkflowRunId,
    string? WorkflowStatus,
    bool Tracked,
    string? Reason = null,
    string? OwnerKind = null,
    string? OwnerId = null);
public record RunnerAgentSessionKey(string ProjectId, string WorkflowRunId, string SessionName);
public record RunnerAgentSessionResponse(RunnerAgentSessionKey Key, [property: JsonPropertyName("runtimeSessionId")] string? AgentSessionId, string Status, string? WorkDir = null, string? Model = null, string? ResolvedModel = null, string? Runtime = null);
public record AgentSessionOpenRequest(
    string? WorkId = null,
    string? WorkType = null,
    string? Stage = null,
    string? Title = null,
    int? IssueNumber = null,
    string? WorkDir = null,
    int? EpicNumber = null);
/// <summary>
/// Body for the runner's <c>POST /api/runner/{runnerId}/agent-sessions/{projectId}/{sessionId}/open</c>
/// call. Generic (non-workflow) AgentSessions are identified by
/// (projectId, sessionId); the launch endpoint already minted the session
/// with source-kind=agent-launch labels, so this request only contributes
/// optional annotations.
/// </summary>
public record GenericAgentSessionOpenRequest(
    string? WorkId = null,
    string? WorkType = null,
    string? Stage = null,
    string? Title = null,
    int? IssueNumber = null,
    string? WorkDir = null);
/// <summary>
/// Wire shape returned to the runner for generic AgentSession endpoints
/// (issue-129 T-002/T-003). Mirrors the workflow response shape but drops
/// the (projectId, workflowRunId, sessionName) key — generic sessions are
/// addressed solely by sessionId.
/// </summary>
public record RunnerGenericAgentSessionResponse(
    [property: JsonPropertyName("runtimeSessionId")] string? AgentSessionId,
    string Status,
    string? WorkDir = null,
    string? Model = null,
    string? ResolvedModel = null,
    string? Runtime = null);
public record AgentSessionAttachRequest(
    string RuntimeSessionId,
    string? Model = null,
    string? WorkDir = null,
    string? ChangeDir = null,
    int? ProcessPid = null,
    string? WorkId = null,
    string? AgentJobId = null);
public record AgentSessionRuntimeEventsRequest(string? WorkId, string? WorkType, string? Stage, IReadOnlyList<AgentSessionRuntimeEventRequest> RuntimeEvents, string? RuntimeSessionId = null);
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
    int? IssueNumber = null,
    int? EpicNumber = null,
    string? Artifacts = null,
    string? SetVars = null,
    string? OwnerKind = null,
    string? AgentJobId = null,
    /// <summary>
    /// AgentSession id for the dispatch envelope. Set for agent-job
    /// dispatches whose launch minted a generic (non-workflow)
    /// AgentSession; the runner uses it verbatim as the session
    /// identity for runtime events. Null for workflow dispatches and
    /// raw-prompt-only AgentJob validation dispatches.
    /// </summary>
    string? AgentSessionId = null,
    string? Recovery = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RecoveryRemaining = null,
    string? Expect = null,
    ParentIssueContextResponse? ParentIssueContext = null);

public sealed record ParentIssueContextResponse(string Title, string? Body);

/// <summary>
/// Poll response carrying zero or more dispatches. Replaces the old single-
/// dispatch 200/204 contract: a reconciliation round may render redeliveries plus
/// new claims, so the response is a list. An empty list is returned as HTTP
/// 204 by the route handler.
/// </summary>
public record RunnerPollResponseDto(List<WorkDispatchResponse> Dispatches);

/// <summary>
/// Wire shape for the workspace cleanup policy that the server hands the
/// runner via <c>GET /api/runner/{runnerId}/config</c>. Each nullable
/// field is an explicit unlimited/disabled sentinel — the runner treats
/// <c>null</c> as "do not evict by this strategy". The server never
/// scans runner filesystems; this DTO only describes policy, never
/// actions.
/// </summary>
public record CleanupPolicyDto(
    int? RetentionDays = null,
    long? StorageBudgetBytes = null,
    long? StorageTargetWatermarkBytes = null);

/// <summary>
/// Body for <c>GET /api/runner/{runnerId}/config</c> — the dedicated
/// runner config channel. Always returns <c>200 OK</c> with this body
/// (never 204), independent of whether <c>POST /poll</c> currently has
/// work to dispatch; the runner is expected to poll this endpoint on
/// its own cadence and treat a missing body / 204 as "policy
/// unavailable". The wrapper (rather than returning
/// <see cref="CleanupPolicyDto"/> bare) leaves room for additional
/// runner-facing config fields to be added additively.
/// </summary>
public record RunnerConfigResponse(CleanupPolicyDto? CleanupPolicy);

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
