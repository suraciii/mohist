using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Services;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workspace.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    public static WebApplication MapRunnerRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/{runnerId}").RequireScopes(Scope.Runner);

        group.MapPost("/register", async (string runnerId, RunnerRegisterRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.ProcessGeneration))
                return ApiResults.BadRequest("processGeneration is required");
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.RegisterAsync(new RunnerInfo(
                runnerId,
                req.Capabilities,
                req.Hostname ?? Environment.MachineName,
                req.ProjectId,
                req.CoderModels,
                BuildGitHash: NormalizeBuildGitHash(req.BuildGitHash),
                CoderModelVariants: NormalizeCoderModelVariants(req.CoderModelVariants),
                ActionCatalog: req.ActionCatalog,
                RuntimeCatalogs: NormalizeRuntimeCatalogs(req.RuntimeCatalogs),
                Component: NormalizeIdentity(req.Component),
                Version: NormalizeIdentity(req.Version),
                SourceRevision: NormalizeIdentity(req.SourceRevision) ?? NormalizeBuildGitHash(req.BuildGitHash),
                TreeHash: NormalizeIdentity(req.TreeHash),
                ArtifactDigest: NormalizeIdentity(req.ArtifactDigest),
                ReleaseId: NormalizeIdentity(req.ReleaseId),
                Generation: req.Generation > 0 ? req.Generation : null), req.ProcessGeneration);
            return Results.Ok();
        });

        group.MapPost("/unregister", async (string runnerId, IGrainFactory grains) =>
        {
            var runner = grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.UnregisterAsync();
            return Results.Ok();
        });

        group.MapPost("/heartbeat", HandleHeartbeatAsync);
        MapRunnerManagerExecutionRoutes(app);
        MapUpdateInterruptRoutes(group);
        MapRunnerUpdateRecoveryRoutes(group);
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
            IssueQuerier issues, RunnerConnectionTracker connections,
            ManagerExecutionCapabilityIssuer managerCredentials,
            IManagerDeploymentEpoch managerEpoch,
            CancellationToken ct) =>
        {
            if (!managerEpoch.Available)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            request.HttpContext.Response.Headers["X-Mohist-Manager-Deployment-Epoch"] = managerEpoch.Current;
            RunnerPollRequest? req;
            try
            {
                req = await request.ReadFromJsonAsync<RunnerPollRequest>(cancellationToken: ct);
            }
            catch
            {
                return ApiResults.BadRequest("invalid poll body");
            }
            if (req is null || string.IsNullOrWhiteSpace(req.ProcessGeneration))
                return ApiResults.BadRequest("processGeneration is required");
            var response = await dispatch.PollAsync(runnerId, connections.ApplyPollAdmission(runnerId, req), ct);
            if (response.Dispatches.Count == 0) return Results.NoContent();

            var dispatches = await Task.WhenAll(response.Dispatches.Select(work =>
                ToWorkDispatchResponseAsync(work, issues.GetParentIssueContextAsync, managerCredentials)));
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
        // present-null or absent, but the config endpoint contract
        // requires the present-null form so the
        // response is self-describing. The override is local to this
        // handler; /poll no longer carries CleanupPolicy.
        // Per-request re-bind from the currently-loaded IConfiguration.
        // IOptions<T> would snapshot once at startup; IOptionsSnapshot<T>
        // is request-scoped (matches the minimal-API handler lifetime),
        // rebuilds every request through OptionsFactory<T>, and honors
        // every registered IConfigureOptions<T> in registration order.
        // Combined with the native-AddJsonFile wiring, a reload
        // of config.jsonc reaches the next /config call without a
        // server restart. No singleton consumes CleanupPolicyOptions
        // today, so IOptionsMonitor is unnecessary machinery.
        group.MapGet("/config", (Microsoft.Extensions.Options.IOptionsSnapshot<CleanupPolicyOptions> cleanupPolicyOptions) =>
        {
            return Results.Json(
                new RunnerConfigResponse(ToCleanupPolicyDto(cleanupPolicyOptions.Value)),
                RunnerConfigJsonOptions);
        });

        MapReportRoute(group);

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

        group.MapGet("/agent-sessions/reconcile", async (
            string runnerId,
            AgentSessionReconcileQuerier sessions,
            CancellationToken ct) =>
        {
            var bindings = await sessions.ListByRunnerAsync(runnerId, ct);
            return Results.Ok(bindings.Select(binding => new RunnerAgentSessionReconcileResponse(
                binding.SessionId,
                binding.Runtime,
                binding.RuntimeSessionId,
                binding.WorkDir)));
        });

        group.MapPost("/agent-sessions/{sessionId}/reconcile-missing", async (
            string runnerId, string sessionId,
            MissingRuntimeSessionRecoveryRequest req,
            AgentSessionResolver sessions) =>
        {
            if (!string.Equals(runnerId, req.ExpectedRunnerId, StringComparison.Ordinal))
                return ApiResults.BadRequest("expectedRunnerId must match the route runnerId", "runner_mismatch");
            var grain = sessions.GetGrain(sessionId);
            if (await grain.GetAsync() is null)
                return ApiResults.NotFound($"Agent session {sessionId} not found");
            try
            {
                var session = await grain.ReconcileMissingBindingAsync(new ReconcileMissingBindingCommand(
                    req.ExpectedRunnerId, req.ExpectedRuntime, req.ExpectedRuntimeSessionId, req.ReplacementRuntimeSessionId));
                return Results.Ok(new RunnerAgentSessionReconcileResponse(
                    session.Id,
                    session.Runtime ?? string.Empty,
                    session.AgentSessionId ?? string.Empty,
                    session.WorkDir ?? string.Empty));
            }
            catch (StaleRuntimeSessionBindingException ex)
            {
                return ApiResults.Conflict(ex.Message, "stale_binding", new { sessionId = ex.SessionId });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "agent_session_recovery_conflict");
            }
        });

        group.MapPost("/agent-sessions/{sessionId}/runtime-events", async (
            string runnerId, string sessionId,
            AgentSessionRuntimeEventsRequest req,
            AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery,
            ManagerExecutionCapabilityIssuer managerCredentials,
            CancellationToken ct) =>
        {
            var grain = sessions.GetGrain(sessionId);
            var existing = await grain.GetAsync();
            if (existing is null || !string.Equals(existing.RunnerId, runnerId, StringComparison.Ordinal))
                return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (string.IsNullOrWhiteSpace(req.RuntimeSessionId))
                return ApiResults.BadRequest("runtimeSessionId is required", "runtime_session_id_required");
            var hasSessionTurnIdentity = !string.IsNullOrWhiteSpace(req.AgentSessionId)
                || !string.IsNullOrWhiteSpace(req.AgentTurnId);
            if (hasSessionTurnIdentity
                && (string.IsNullOrWhiteSpace(req.AgentSessionId)
                    || string.IsNullOrWhiteSpace(req.AgentTurnId)))
            {
                return ApiResults.BadRequest("Session runtime events require AgentSession and Agent turn identity", "session_runtime_identity_required");
            }
            if (hasSessionTurnIdentity
                && !string.Equals(req.AgentSessionId, sessionId, StringComparison.Ordinal))
            {
                return ApiResults.Conflict("AgentSession changed before Session runtime-event delivery", "agent_session_changed");
            }
            if (!string.IsNullOrWhiteSpace(req.InputDeliveryId)
                || !string.IsNullOrWhiteSpace(req.ActionAttemptId)
                || !string.IsNullOrWhiteSpace(req.WorkId)
                || !string.IsNullOrWhiteSpace(req.Runtime))
            {
                return ApiResults.BadRequest("Task execution identity is not accepted on the Session runtime-event route", "session_runtime_task_identity_invalid");
            }

            var runtimeEvents = req.RuntimeEvents.Select(e => new AgentSessionRuntimeEventInput(
                e.Type,
                e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            var events = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
                runtimeEvents,
                req.RuntimeSessionId,
                SessionTurnId: req.AgentTurnId));
            if (await IsManagerAgentSessionAsync(sessionQuery, sessionId, ct))
                RevokeCompletedManagerFollowupLeases(sessionId, req.RuntimeEvents, managerCredentials);
            return Results.Ok(events);
        });

        // AgentJob AgentSession routes identify the persisted Session by
        // (projectId, sessionId) regardless of launch origin.
        group.MapGet("/agent-sessions/{projectId}/{sessionId}", async (
            string projectId, string sessionId,
            AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery,
            CancellationToken ct) =>
        {
            var session = await sessions.GetGrain(sessionId).GetAsync();
            if (session is null) return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (!await IsAgentJobSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
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
            if (!await IsAgentJobSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");
            // The AgentSession was pre-created by its launch origin, carrying
            // project and source labels. The runner's open call only contributes
            // annotations (workId/workType/stage/title/issueNumber) for traceability
            // — labels are intentionally left untouched so the pre-created
            // identity (projectId, agentId, agentName, source-kind) is
            // preserved by AgentSessionMetadata.Merge.
            //
            // The session's own runtime is authoritative: the launch
            // endpoint pinned the backend at
            // launch time, so we read it back from the session rather
            // than hardcoding opencode.
            var session = await grain.OpenAsync(new OpenAgentSessionCommand(
                runnerId,
                existing.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
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
            if (!await IsAgentJobSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");

            try
            {
                // the session's persisted runtime is
                // authoritative for the generic path. The launch endpoint
                // pinned it at launch time; attach binds the physical
                // session to that backend rather than a hardcoded literal.
                var session = await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                    req.RuntimeSessionId, req.Model, req.WorkDir, req.ChangeDir, req.ProcessPid,
                     Runtime: existing.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
                     ExpectedRuntime: req.ExpectedRuntime,
                     ExpectedAgentSessionId: req.ExpectedRuntimeSessionId,
                     ExpectedRunnerId: req.ExpectedRunnerId));
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

        group.MapPost("/agent-sessions/{projectId}/{sessionId}/recover-missing", async (
            string projectId, string sessionId,
            MissingRuntimeSessionRecoveryRequest req, AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery,
            CancellationToken ct) =>
        {
            var grain = sessions.GetGrain(sessionId);
            if (await grain.GetAsync() is null || !await IsAgentJobSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");
            try
            {
                var session = await grain.RecoverMissingRuntimeSessionAsync(new RecoverMissingRuntimeSessionCommand(
                    req.ExpectedRunnerId,
                    req.ExpectedRuntime,
                    req.ExpectedRuntimeSessionId,
                    req.ReplacementRuntimeSessionId,
                    req.ExpectedQueuedTurnId));
                return Results.Ok(ToRunnerGenericAgentSession(session));
            }
            catch (StaleRuntimeSessionBindingException ex)
            {
                return ApiResults.Conflict(ex.Message, "stale_binding", new { sessionId = ex.SessionId });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "agent_session_recovery_conflict");
            }
        });

        group.MapPost("/agent-sessions/{projectId}/{sessionId}/runtime-events", async (
            string projectId, string sessionId,
            AgentSessionRuntimeEventsRequest req, AgentSessionResolver sessions,
            AgentSessionQuery sessionQuery,
            AgentSessionFollowupDispatcher followups,
            ManagerExecutionCapabilityIssuer managerCredentials,
            CancellationToken ct) =>
        {
            var grain = sessions.GetGrain(sessionId);
            var existing = await grain.GetAsync();
            if (existing is null) return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (!await IsAgentJobSessionInProjectAsync(sessionQuery, projectId, sessionId, ct))
                return ApiResults.NotFound($"Agent session {sessionId} not found");
            if (string.IsNullOrWhiteSpace(req.RuntimeSessionId))
                return ApiResults.BadRequest("runtimeSessionId is required", "runtime_session_id_required");

            var runtimeEvents = req.RuntimeEvents.Select(e => new AgentSessionRuntimeEventInput(
                e.Type,
                e.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToArray();
            var events = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(runtimeEvents, req.RuntimeSessionId));
            if (string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal))
            {
                RevokeCompletedManagerFollowupLeases(sessionId, req.RuntimeEvents, managerCredentials);
                if (ContainsManagerCredentialExpiry(req.RuntimeEvents))
                    await grain.EnsureManagerCredentialExpiryRecoveryAsync();
            }
            await followups.DispatchNextAsync(projectId, sessionId, ct);
            return Results.Ok(events);
        });

        // Runner reports a materialized named workspace directory; the
        // grain records the home (first writer wins) so later dispatches
        // bind to this runner.
        group.MapPost("/workspaces/{projectId}/{workspaceName}/materialized", async (
            string runnerId,
            string projectId,
            string workspaceName,
            WorkspaceMaterializedRequest req,
            IGrainFactory grains,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Path))
                return ApiResults.BadRequest("path is required.", "workspace_materialization_invalid");
            try
            {
                var home = await grains.GetGrain<Mohist.Server.Workspace.Grains.IWorkspaceGrain>(
                        GrainKey.Workspace(projectId, workspaceName))
                    .EnsureMaterializedOnAsync(runnerId, req.Path, time.GetUtcNow());
                return home is null
                    ? ApiResults.NotFound($"Workspace '{workspaceName}' not found")
                    : ApiResults.Ok(new WorkspaceMaterializedResponse(home.RunnerId, home.Path));
            }
            catch (Mohist.Server.Workspace.Domain.WorkspaceDomainException ex)
            {
                var details = ex.Hint is null ? null : new { hint = ex.Hint };
                var status = string.Equals(ex.Code, "workspace_home_claimed", StringComparison.Ordinal)
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status400BadRequest;
                return ApiResults.Fail(ex.Message, status, ex.Code, details);
            }
        });

        // Runner-scoped lifecycle observation for the named-workspace
        // cleanup guard: the runner cannot observe archive state or
        // bound-session activity locally, so the cleanup probe asks the
        // server. `status` is the Workspace lifecycle status; the count
        // is sessions bound to and actively using the workspace (the
        // same predicate the lifecycle-mutation guard uses).
        group.MapGet("/workspaces/{projectId}/{workspaceName}/reclaimable", async (
            string projectId,
            string workspaceName,
            WorkspaceQuerier querier,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            var workspace = grains.GetGrain<Mohist.Server.Workspace.Grains.IWorkspaceGrain>(
                GrainKey.Workspace(projectId, workspaceName));
            var state = await workspace.GetAsync();
            if (state is null)
                return ApiResults.NotFound($"Workspace '{workspaceName}' not found");
            var activeBoundSessions = await querier.CountActiveBoundSessionsAsync(projectId, workspaceName, ct);
            return ApiResults.Ok(new WorkspaceReclaimableResponse(
                state.Status == Mohist.Server.Workspace.Domain.WorkspaceStatus.Active ? "active" : "archived",
                activeBoundSessions));
        });

        return app;
    }

    private static RunnerGenericAgentSessionResponse ToRunnerGenericAgentSession(AgentSessionInfo session) =>
        new(
            session.AgentSessionId,
            session.Status,
            session.WorkDir,
            session.Model,
            session.ResolvedModel,
            session.Runtime);

    private static async Task<bool> IsAgentJobSessionInProjectAsync(
        AgentSessionQuery sessionQuery,
        string projectId,
        string sessionId,
        CancellationToken ct)
    {
        var records = await sessionQuery.ListByIdsAsync([sessionId], ct);
        var record = records.FirstOrDefault();
        if (record is null) return false;
        if (!string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal))
            return false;

        return record.Label(AgentSessionQueryMetadataKeys.SourceKind) is
            "agent-launch" or "agent-connection" or "workflow";
    }

    /// <summary>
    /// Builds the annotations-only metadata that the runner contributes on
    /// open for a generic AgentSession. Labels are intentionally left null
    /// so the pre-created labels (source-kind, agent-id, agent-name,
    /// project-id) are preserved by
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

    internal static bool NeedsFreshRuntimeSession(string? runtimeSessionId, string? lastTerminalStatus) =>
        !string.IsNullOrWhiteSpace(runtimeSessionId)
        && lastTerminalStatus is not null
        && (string.Equals(lastTerminalStatus, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lastTerminalStatus, "aborted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lastTerminalStatus, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lastTerminalStatus, "timeout", StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeBuildGitHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static string? NormalizeIdentity(string? value) => NormalizeBuildGitHash(value);

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
    /// present-null or absent, but the config endpoint contract
    /// requires the present-null form so the
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
    string? ProcessGeneration = null,
    string? ProjectId = null,
    string? Hostname = null,
    string[]? CoderModels = null,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null,
    ActionCatalog? ActionCatalog = null,
    Dictionary<string, RuntimeCatalogEntry>? RuntimeCatalogs = null,
    string? Component = null,
    string? Version = null,
    string? SourceRevision = null,
    string? TreeHash = null,
    string? ArtifactDigest = null,
    string? ReleaseId = null,
    long? Generation = null);
public record RunnerSlotsPatchRequest(int Slots);
public record RunnerSlotsPatchResponse(string RunnerId, int Slots);
public record RunnerHeartbeatRequest(
    string[]? Capabilities = null,
    string? ProjectId = null,
    string? Hostname = null,
    string[]? CoderModels = null,
    string? BuildGitHash = null,
    Dictionary<string, string[]>? CoderModelVariants = null,
    string? ConnectionId = null,
    ActionCatalog? ActionCatalog = null,
    Dictionary<string, RuntimeCatalogEntry>? RuntimeCatalogs = null,
    string? Component = null,
    string? Version = null,
    string? SourceRevision = null,
    string? TreeHash = null,
    string? ArtifactDigest = null,
    string? ReleaseId = null,
    long? Generation = null);
public record RunnerReportResponse(string Verdict);
public record RunnerAgentSessionReconcileResponse(
    string SessionId,
    string Runtime,
    string RuntimeSessionId,
    string WorkDir);
public record RunnerAgentSessionKey(string ProjectId, string WorkflowRunId, string SessionName);
public record RunnerAgentSessionResponse(RunnerAgentSessionKey Key, string SessionId, [property: JsonPropertyName("runtimeSessionId")] string? AgentSessionId, string Status, string? WorkDir = null, string? Model = null, string? ResolvedModel = null, string? Runtime = null, bool NeedsFreshRuntimeSession = false);
public record AgentSessionOpenRequest(
    string? WorkId = null,
    string? WorkType = null,
    string? Stage = null,
    string? Title = null,
    int? IssueNumber = null,
    string? WorkDir = null,
    int? EpicNumber = null,
    string? Runtime = null);
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
///. Mirrors the workflow response shape but drops
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
    string? AgentJobId = null,
    string? Runtime = null,
    string? ExpectedRuntime = null,
    string? ExpectedRuntimeSessionId = null,
    string? ExpectedRunnerId = null);
public record MissingRuntimeSessionRecoveryRequest(
    string ExpectedRunnerId,
    string ExpectedRuntime,
    string ExpectedRuntimeSessionId,
    string ReplacementRuntimeSessionId,
    string? ExpectedQueuedTurnId = null);
public record WorkflowAgentSessionResetRequest(
    string ExpectedRunnerId,
    string ExpectedRuntime,
    string ExpectedRuntimeSessionId,
    string ReplacementRuntimeSessionId,
    string ReplacementRuntime = "opencode");
public record AgentSessionRuntimeEventsRequest(
    string? WorkId,
    string? WorkType,
    string? Stage,
    IReadOnlyList<AgentSessionRuntimeEventRequest> RuntimeEvents,
    string? RuntimeSessionId = null,
    string? ActionAttemptId = null,
    string? InputDeliveryId = null,
    string? AgentSessionId = null,
    string? AgentTurnId = null,
    string? Runtime = null);
public record AgentSessionRuntimeEventRequest(string Type, System.Text.Json.JsonElement Payload);
public record RunnerRuntimeEventReceipt(
    string Type,
    string? InputDeliveryId = null,
    string? AgentTurnId = null,
    string? AgentSessionId = null);


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

/// <summary>
/// Body for <c>POST /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/materialized</c>.
/// The runner reports the local directory it materialized for a named
/// workspace; the server records it as the workspace home (first writer
/// wins) so later dispatches bind to this runner.
/// </summary>
public record WorkspaceMaterializedRequest(string? Path);
public record WorkspaceMaterializedResponse(string RunnerId, string Path);

/// <summary>
/// Answer for <c>GET /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/reclaimable</c>.
/// <c>Status</c> is the Workspace lifecycle status; <c>ActiveBoundSessions</c>
/// counts sessions bound to and actively using the workspace.
/// </summary>
public sealed record WorkspaceReclaimableResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("activeBoundSessions")] int ActiveBoundSessions);
