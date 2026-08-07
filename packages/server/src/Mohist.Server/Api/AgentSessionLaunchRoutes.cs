using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Infrastructure;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Product launch endpoint for a generic AgentSession from a project-scoped
/// Agent profile (composed through the shared
/// <see cref="IAgentLauncher"/>). Distinct from the
/// validation-only <c>POST /api/agent-jobs/validate</c> route, which
/// remains a developer smoke-test surface and is not the product API.
/// </summary>
/// <remarks>
/// <para>
/// The route delegates the canonical mint-session → open-generic-session
/// → build-AgentJobInput → submit-to-grain pipeline to
/// <see cref="IAgentLauncher"/>. The route keeps its domain-level
/// gates (whitespace prompt → 400; unresolved agent → 404; archived agent
/// → 409) and composes the 201 response from
/// <see cref="AgentLaunchResult"/> (carrying both the AgentJob key and the
/// AgentSession id) plus the project-scoped transcript URL and job-result
/// URL (product surfaces owned by the API layer, not the launcher).
/// </para>
/// <para>
/// The launch body is bound through <see cref="AgentSessionLaunchBody"/>'s
/// raw-JSON presence binder, which rejects every undeclared top-level
/// field before the Agent lookup so caller-supplied execution-backend
/// overrides cannot alter a named Agent's runtime. The binder accepts
/// only <c>prompt</c> and <c>context</c>; any extra property surfaces an
/// actionable 400 before any session or job is created.
/// </para>
/// </remarks>
public static class AgentSessionLaunchRoutes
{
    internal static readonly IReadOnlySet<string> AllowedTopLevelFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "prompt",
        "context",
        "attachments",
    };

    public static WebApplication MapAgentSessionLaunchRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agents/{agentRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/sessions", async (
            HttpContext context,
            string projectRef,
            string agentRef,
            AgentSessionLaunchBody body,
            AgentQuerier agentQuerier,
            IssueQuerier issueQuerier,
            EpicQuerier epicQuerier,
            AgentReadinessService readiness,
            IAgentLauncher launcher,
            AttachmentService attachments,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (body is null)
            {
                return ApiResults.BadRequest(
                    "request body is required",
                    "body_required");
            }

            if (body.UndeclaredFields.Count > 0)
            {
                return ApiResults.BadRequest(
                    $"unsupported top-level field(s): {string.Join(", ", body.UndeclaredFields)}; " +
                    "the launch body accepts only prompt, context, and attachments.",
                    "unsupported_field",
                    new { fields = body.UndeclaredFields.ToArray() });
            }

            var prompt = body.Prompt;
            var hasText = !string.IsNullOrWhiteSpace(prompt);

            var idempotencyKey = ReadIdempotencyKey(context.Request);
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return ApiResults.BadRequest(
                    "Idempotency-Key is required for manual agent launches",
                    "idempotency_key_required",
                    new { fields = new[] { "Idempotency-Key" } });
            }

            var project = context.GetResolvedProject();

            // Explicit Project-backed launch: when the caller supplies both a
            // repository name and a workspacePath, resolve the Project
            // Repository snapshot now and fail-fast on an unknown name so
            // the launch never creates a permanent unconfirmed Session. A
            // repository without a workspacePath (or vice versa) is
            // intentionally not a Project-backed source — the Session gets
            // no WorkspaceRepository and can never become a confirmed
            // worktree parent.
            WorkspaceRepositorySnapshot? workspaceRepositorySnapshot = null;
            if (!string.IsNullOrWhiteSpace(body.Context?.Repository)
                && !string.IsNullOrWhiteSpace(body.Context?.WorkspacePath))
            {
                var repository = project.GetRepository(body.Context!.Repository);
                if (repository is null)
                {
                    return ApiResults.BadRequest(
                        $"Unknown repository '{body.Context!.Repository}' for project '{project.Id}'.",
                        "repository_not_found",
                        new { fields = new[] { "context.repository" } });
                }
                workspaceRepositorySnapshot = new WorkspaceRepositorySnapshot(
                    repository.Name,
                    repository.GitUrl,
                    repository.ResolvedBaseBranch);
            }

            // The fingerprint folds the caller-submitted attachment ids
            // (raw, in order) so a replay with a different attachment set
            // is rejected as a conflicting idempotency replay. The
            // accepted subset is what the dispatch envelope carries and
            // is established later via ValidateAndBindAgentInputAsync.

            IReadOnlyList<WorkspaceRepositorySnapshot>? workspaceRepositories = null;
            if (!string.IsNullOrWhiteSpace(body.Context?.Workspace))
            {
                var wsGrain = grains.GetGrain<Mohist.Server.Workspace.Grains.IWorkspaceGrain>(
                    Infrastructure.Orleans.GrainKey.Workspace(project.Id, body.Context.Workspace.Trim()));
                var ws = await wsGrain.GetAsync();
                if (ws is { RepositoryNames.Count: > 0 })
                {
                    var repos = project.Repositories;
                    workspaceRepositories = ws.RepositoryNames
                        .Select(name => repos.FirstOrDefault(r =>
                            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                        .Where(r => r is not null)
                        .Select(r => new WorkspaceRepositorySnapshot(r!.Name, r.GitUrl, r.ResolvedBaseBranch))
                        .ToList();
                }
            }

            var launchRequest = new AgentLaunchCoordinatorRequest(
                Prompt: prompt?.Trim() ?? string.Empty,
                AgentRef: agentRef,
                Runtime: null,
                WorkspaceName: body.Context?.Workspace,
                WorkspacePath: body.Context?.WorkspacePath,
                IssueNumber: body.Context?.IssueNumber,
                EpicNumber: body.Context?.EpicNumber,
                Repository: body.Context?.Repository,
                Title: null,
                AttachmentIds: body.Attachments?.ToArray(),
                WorkspaceRepositories: workspaceRepositories);

            // Resume first: a replay that conflicts on the existing
            // fingerprint (prompt, attachments, context) must surface as
            // 409 launch_idempotency_conflict, not a 400 input_required.
            try
            {
                var resumed = await launcher.ResumeIdempotentAsync(
                    project.Id,
                    idempotencyKey,
                    launchRequest,
                    ct);
                if (resumed is not null)
                    return AcceptedLaunch(project.Id, resumed, attachmentResults: null);
            }
            catch (LaunchIdempotencyConflictException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "launch_idempotency_conflict",
                    new { idempotencyKey = ex.IdempotencyKey });
            }
            catch (LaunchSetupPendingException ex)
            {
                return LaunchSetupPending(ex);
            }
            catch (AgentReadinessException ex)
            {
                return ReadinessRejected(ex);
            }

            var hasAttachments = body.Attachments is { Count: > 0 };
            if (!hasText && !hasAttachments)
            {
                return ApiResults.BadRequest(
                    "input requires non-empty prompt or at least one accepted attachment",
                    "input_required",
                    new { fields = new[] { "prompt", "attachments" } });
            }

            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
            {
                return ApiResults.NotFound($"Agent '{agentRef}' not found");
            }
            if (string.Equals(agent.Status, AgentStatus.Archived, StringComparison.Ordinal))
            {
                return ApiResults.Conflict("Archived agents cannot start new sessions", "agent_archived");
            }

            var contextError = await ValidateContextAsync(body.Context, project.Id, issueQuerier, epicQuerier, grains);
            if (contextError is not null)
                return contextError;

            try
            {
                await readiness.EnsureLaunchableAsync(project.Id, agent, ct);
            }
            catch (AgentReadinessException ex)
            {
                return ReadinessRejected(ex);
            }

            // The route mints every identity used by attachment ownership.
            // The coordinator persists and adopts them as its canonical plan,
            // so a scoped content read always names the durable SessionInput.
            var ownershipIdentity = $"{project.Id}\n{idempotencyKey}";
            var preMintedSessionId = $"agent-session-{AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nsession")}";
            var preMintedInputId = AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\ninput");
            var preMintedTurnId = Guid.NewGuid().ToString("N");

            AgentInputAttachmentAcceptanceBatch attachmentBatch;
            try
            {
                attachmentBatch = await attachments.ValidateAndBindAgentInputAsync(
                    project.Id,
                    agentSessionId: preMintedSessionId,
                    inputId: preMintedInputId,
                    body.Attachments,
                    ct);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.BadRequest(ex.Message, "attachment_limit_exceeded",
                    new { fields = new[] { "attachments" } });
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "attachment_invalid",
                    new { fields = new[] { "attachments" } });
            }

            if (attachmentBatch.AcceptedCount == 0 && !hasText)
            {
                // All attachments were rejected and there is no text: the
                // input is unusable. Surface the per-file rejection
                // reasons in the response so the caller sees exactly why.
                return ApiResults.BadRequest(
                    "input has no usable content: all attachments were rejected",
                    "input_unusable",
                    new
                    {
                        fields = new[] { "prompt", "attachments" },
                        attachments = attachmentBatch.Results
                            .Select(BuildAttachmentResultDto)
                            .ToArray(),
                    });
            }

            var launchContext = new AgentLaunchContext(
                ProjectId: project.Id,
                IssueNumber: body.Context?.IssueNumber,
                EpicNumber: body.Context?.EpicNumber,
                Repository: body.Context?.Repository,
                WorkspacePath: body.Context?.WorkspacePath,
                WorkspaceName: body.Context?.Workspace,
                Title: null,
                WorkspaceRepository: workspaceRepositorySnapshot);

            AgentLaunchResult result;
            var retainNewlyBoundAttachments = false;
            Task RollbackNewlyBoundAttachmentsAsync() => attachments.UnbindAgentInputAsync(
                project.Id,
                preMintedSessionId,
                preMintedInputId,
                attachmentBatch.NewlyBoundAttachmentIds ?? [],
                CancellationToken.None);
            try
            {
                result = await launcher.LaunchIdempotentAsync(
                    agent,
                    prompt ?? string.Empty,
                    launchContext,
                    idempotencyKey,
                    launchRequest,
                    attachmentBatch.Results
                        .Where(r => r.IsAccepted && r.Descriptor is not null)
                        .Select(r => r.Descriptor!)
                        .ToArray(),
                    preMintedSessionId,
                    preMintedInputId,
                    preMintedTurnId,
                    ct);
                retainNewlyBoundAttachments = true;
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "validation_failed");
            }
            catch (LaunchIdempotencyConflictException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "launch_idempotency_conflict",
                    new { idempotencyKey = ex.IdempotencyKey });
            }
            catch (LaunchSetupPendingException ex)
            {
                retainNewlyBoundAttachments = true;
                return LaunchSetupPending(ex);
            }
            catch (AgentReadinessException ex)
            {
                return ReadinessRejected(ex);
            }
            finally
            {
                if (!retainNewlyBoundAttachments)
                    await RollbackNewlyBoundAttachmentsAsync();
            }

            return AcceptedLaunch(project.Id, result, attachmentBatch.Results);
        });

        return app;
    }

    private static object BuildAttachmentResultDto(AgentInputAttachmentAcceptance acceptance) =>
        acceptance.IsAccepted
            ? (object)new
            {
                id = acceptance.Id,
                accepted = true,
                name = acceptance.Descriptor!.OriginalFileName,
                contentType = acceptance.Descriptor.ContentType,
                size = acceptance.Descriptor.Size,
            }
            : new
            {
                id = acceptance.Id,
                accepted = false,
                reason = acceptance.RejectionReason?.ToString(),
                message = acceptance.RejectionMessage,
            };

    private static IResult AcceptedLaunch(
        string projectId,
        AgentLaunchResult result,
        IReadOnlyList<AgentInputAttachmentAcceptance>? attachmentResults = null)
    {
        var acceptedAttachments = attachmentResults?
            .Where(r => r.IsAccepted)
            .Select(r => new AgentSessionLaunchAttachment(
                r.Id,
                r.Descriptor!.OriginalFileName,
                r.Descriptor.ContentType,
                r.Descriptor.Size))
            .ToArray();
        var rejectedAttachments = attachmentResults?
            .Where(r => !r.IsAccepted)
            .Select(r => new AgentSessionLaunchAttachmentRejection(
                r.Id,
                r.RejectionReason?.ToString() ?? "unknown",
                r.RejectionMessage ?? "Attachment was rejected."))
            .ToArray();

        return Results.Json(
                new ApiResponse<AgentSessionLaunchResponse>(
                    true,
                    new AgentSessionLaunchResponse(
                        JobId: result.JobKey,
                        SessionId: result.SessionId,
                        InputId: result.InputId,
                        TurnId: result.TurnId,
                        AgentId: result.AgentId,
                        AgentName: result.AgentName,
                        Status: "queued",
                        Attachments: acceptedAttachments,
                        RejectedAttachments: rejectedAttachments,
                        TranscriptUrl: $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-sessions/{Uri.EscapeDataString(result.SessionId)}/transcript",
                        JobUrl: $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-jobs/{Uri.EscapeDataString(result.JobKey)}",
                        ObservationUrl: $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-jobs/{Uri.EscapeDataString(result.JobKey)}/launch-observation")),
                statusCode: 201);
    }

    private static IResult LaunchSetupPending(LaunchSetupPendingException exception) =>
        ApiResults.Fail(
            exception.Message,
            StatusCodes.Status503ServiceUnavailable,
            "launch_setup_pending",
            new { idempotencyKey = exception.IdempotencyKey });

    private static IResult ReadinessRejected(AgentReadinessException exception) =>
        ApiResults.Fail(
            exception.Message,
            StatusCodes.Status409Conflict,
            "agent_needs_setup",
            exception.Result);

    private static async Task<IResult?> ValidateContextAsync(
        AgentSessionLaunchContextRef? context,
        string projectId,
        IssueQuerier issueQuerier,
        EpicQuerier epicQuerier,
        IGrainFactory grains)
    {
        if (context?.IssueNumber is <= 0)
            return ApiResults.BadRequest("issueNumber must be positive", "validation_failed");
        if (context?.EpicNumber is <= 0)
            return ApiResults.BadRequest("epicNumber must be positive", "validation_failed");

        if (context?.IssueNumber is int issueNumber
            && await issueQuerier.GetAsync(projectId, issueNumber) is null)
        {
            return ApiResults.NotFound($"Issue #{issueNumber} not found");
        }

        if (context?.EpicNumber is int epicNumber
            && await epicQuerier.GetAsync(projectId, epicNumber) is null)
        {
            return ApiResults.NotFound($"Epic #{epicNumber} not found");
        }

        if (!string.IsNullOrWhiteSpace(context?.Workspace))
        {
            var workspace = await grains.GetGrain<Mohist.Server.Workspace.Grains.IWorkspaceGrain>(
                Infrastructure.Orleans.GrainKey.Workspace(projectId, context.Workspace.Trim())).GetAsync();
            if (workspace is null)
                return ApiResults.BadRequest($"Workspace '{context.Workspace}' not found", "workspace_not_found");
            if (workspace.Status != Mohist.Server.Workspace.Domain.WorkspaceStatus.Active)
                return ApiResults.BadRequest($"Workspace '{context.Workspace}' is archived and cannot accept new sessions", "workspace_archived");
        }

        return null;
    }
    private static string? ReadIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values))
            return null;
        if (values.Count == 0)
            return null;
        var value = values[0];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// Raw-JSON presence-bound body for
/// <c>POST /api/projects/{projectRef}/agents/{agentRef}/sessions</c>.
/// Records every top-level JSON property name so the route can reject
/// callers that try to override the Agent's execution definition
/// (e.g. a <c>runtime</c> field). Only <c>prompt</c> and <c>context</c>
/// are accepted; any other property surfaces an actionable 400 before
/// Agent lookup, AgentSession creation, or AgentJob submission.
/// </summary>
public sealed record AgentSessionLaunchBody(
    string? Prompt,
    AgentSessionLaunchContextRef? Context,
    IReadOnlyList<string>? Attachments,
    IReadOnlyList<string> UndeclaredFields,
    JsonElement Raw)
{
    public static async ValueTask<AgentSessionLaunchBody?> BindAsync(HttpContext context)
    {
        try
        {
            return await BindCoreAsync(context);
        }
        catch (JsonException)
        {
            return new AgentSessionLaunchBody(null, null, null, [], default);
        }
    }

    private static async ValueTask<AgentSessionLaunchBody> BindCoreAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return new AgentSessionLaunchBody(
                Prompt: null,
                Context: null,
                Attachments: null,
                UndeclaredFields: [],
                Raw: raw);
        }

        var undeclared = new List<string>();
        foreach (var property in raw.EnumerateObject())
        {
            if (!AgentSessionLaunchRoutes.AllowedTopLevelFields.Contains(property.Name))
                undeclared.Add(property.Name);
        }

        var prompt = raw.TryGetProperty("prompt", out var promptElement)
                     && promptElement.ValueKind != JsonValueKind.Null
            ? promptElement.ValueKind == JsonValueKind.String
                ? promptElement.GetString()
                : throw new JsonException("prompt must be a string")
            : null;

        AgentSessionLaunchContextRef? ctx = null;
        if (raw.TryGetProperty("context", out var ctxElement)
            && ctxElement.ValueKind == JsonValueKind.Object)
        {
            ctx = new AgentSessionLaunchContextRef(
                IssueNumber: TryReadPositiveInt(ctxElement, "issueNumber"),
                EpicNumber: TryReadPositiveInt(ctxElement, "epicNumber"),
                Repository: TryReadString(ctxElement, "repository"),
                Workspace: TryReadString(ctxElement, "workspace"),
                WorkspacePath: TryReadString(ctxElement, "workspacePath"));
        }

        var attachments = TryReadAttachments(raw);

        return new AgentSessionLaunchBody(
            Prompt: prompt,
            Context: ctx,
            Attachments: attachments,
            UndeclaredFields: undeclared,
            Raw: raw);
    }

    private static IReadOnlyList<string>? TryReadAttachments(JsonElement parent)
    {
        if (!parent.TryGetProperty("attachments", out var attachmentsElement)
            || attachmentsElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (attachmentsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("attachments must be an array of attachment ids");
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in attachmentsElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Null) continue;
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("attachments entries must be strings");
            }
            var raw = entry.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (seen.Add(raw.Trim()))
            {
                ids.Add(raw.Trim());
            }
        }
        return ids.Count == 0 ? null : ids;
    }

    private static string? TryReadString(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new JsonException($"context.{name} must be a string");

    private static int? TryReadPositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new JsonException($"context.{name} must be an integer");
        return number;
    }
}

public sealed record AgentSessionLaunchContextRef(
    int? IssueNumber = null,
    int? EpicNumber = null,
    string? Repository = null,
    string? Workspace = null,
    string? WorkspacePath = null);

/// <summary>
/// Response body for a successful generic AgentSession launch. A launch
/// creates two entities atomically — an <c>AgentJob</c> (the work owner)
/// and an <c>AgentSession</c> (the conversation owner) — so the response
/// surfaces both identities: <see cref="JobId"/> is the AgentJob grain
/// key the launcher minted (the same id the AgentJob read surface
/// accepts — there is no translation gap between launch and read), and
/// <see cref="SessionId"/> is the conversation owner. The agent id and
/// name echo the resolved profile; the status reflects the initial state
/// immediately after dispatch; <see cref="TranscriptUrl"/> points at the
/// product read path for the generic session transcript, and
/// <see cref="JobUrl"/> points at the AgentJob read surface.
/// </summary>
public sealed record AgentSessionLaunchResponse(
    string JobId,
    string SessionId,
    string InputId,
    string TurnId,
    string AgentId,
    string AgentName,
    string Status,
    IReadOnlyList<AgentSessionLaunchAttachment>? Attachments,
    IReadOnlyList<AgentSessionLaunchAttachmentRejection>? RejectedAttachments,
    string TranscriptUrl,
    string JobUrl,
    string ObservationUrl);

/// <summary>
/// Accepted attachment descriptor the launch route surfaces in the
/// 201 response. Mirrors <see cref="AgentSessionInputAttachmentDescriptor"/>
/// minus the server-side <c>AcceptedAt</c> stamp (a launch-time
/// concern the client does not render).
/// </summary>
public sealed record AgentSessionLaunchAttachment(
    string Id,
    string Name,
    string? ContentType,
    long Size);

/// <summary>
/// Per-file rejection surfaced alongside <see cref="AgentSessionLaunchAttachment"/>
/// in the 201 response. The reason is the stable enum name (e.g.
/// <c>"NotFound"</c>, <c>"AlreadyBound"</c>) and the message is the
/// human-readable explanation the caller should render to the user.
/// </summary>
public sealed record AgentSessionLaunchAttachmentRejection(
    string Id,
    string Reason,
    string Message);
