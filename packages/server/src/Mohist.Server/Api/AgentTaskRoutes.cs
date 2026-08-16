using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workspace.Services;
using ProjectInfo = Mohist.Server.Project.Services.ProjectInfo;

namespace Mohist.Server.Api;

/// <summary>
/// Task-first Agent creation and launch. This route owns only the definition
/// orchestration and pre-create validation; execution is delegated to the
/// same idempotent launcher used by definition-first sessions.
/// </summary>
public static class AgentTaskRoutes
{
    internal static readonly IReadOnlySet<string> AllowedTopLevelFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "prompt",
        "attachments",
        "context",
        "name",
        "runtime",
        "model",
        "variant",
    };

    private const int MaxNameRaceRetries = 3;

    public static WebApplication MapAgentTaskRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agent-tasks")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("", async (
            HttpContext context,
            AgentTaskBody body,
            AgentQuerier agentQuerier,
            IssueQuerier issueQuerier,
            EpicQuerier epicQuerier,
            IAgentLauncher launcher,
            AttachmentService attachments,
            AgentTaskDefinitionFactory definitions,
            IGrainFactory grains,
            InteractionWorkspaceProvisioner provisioner,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (body is null)
                return ApiResults.BadRequest("request body is required", "body_required");

            if (body.BindingError is not null)
                return ApiResults.BadRequest(body.BindingError, "validation_failed");

            if (body.UndeclaredFields.Count > 0)
            {
                return ApiResults.BadRequest(
                    $"unsupported top-level field(s): {string.Join(", ", body.UndeclaredFields)}; "
                    + "the task body accepts only prompt, attachments, context, name, runtime, model, and variant.",
                    "unsupported_field",
                    new { fields = body.UndeclaredFields.ToArray() });
            }

            var idempotencyKey = AgentSessionLaunchRoutes.ReadIdempotencyKey(context.Request);
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return ApiResults.BadRequest(
                    "Idempotency-Key is required for task-first Agent launches",
                    "idempotency_key_required",
                    new { fields = new[] { "Idempotency-Key" } });
            }

            var project = context.GetResolvedProject();
            var ownershipIdentity = $"{project.Id}\n{idempotencyKey}";
            var preMintedAgentId = $"agent_{AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nagent")}";
            var preMintedSessionId = $"agent-session-{AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nsession")}";
            var preMintedInputId = AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\ninput");
            var preMintedTurnId = AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nturn");
            var launchOrigin = AgentSessionLaunchRoutes.ReadLaunchOrigin(context.Request);
            var suppliedWorkspace = body.Context?.Workspace?.Trim();
            var hasExplicitWorkspace = suppliedWorkspace is { Length: > 0 };
            var workspaceName = hasExplicitWorkspace
                ? suppliedWorkspace!
                : launchOrigin == "cli"
                    ? await provisioner.ResolveCliWorkspaceNameAsync(project.Id)
                    : await provisioner.ResolveWebWorkspaceNameAsync(project.Id, preMintedSessionId);

            var workspaceRepositories = await ResolveWorkspaceRepositoriesAsync(
                project,
                workspaceName,
                grains);

            // AgentRef is deliberately the caller's name hint only. The
            // derived Agent id/name are envelope data, not replay inputs.
            var requestedTargetId = body.Context?.TargetId?.Trim() is { Length: > 0 } targetId
                ? targetId
                : null;
            var launchRequest = new AgentLaunchCoordinatorRequest(
                Prompt: body.Prompt?.Trim() ?? string.Empty,
                AgentRef: NormalizeOptional(body.Name),
                Runtime: NormalizeOptional(body.Runtime),
                WorkspacePath: body.Context?.WorkspacePath,
                IssueNumber: body.Context?.IssueNumber,
                EpicNumber: body.Context?.EpicNumber,
                Repository: body.Context?.Repository,
                Title: null,
                AttachmentIds: body.Attachments?.ToArray(),
                WorkspaceName: workspaceName,
                Origin: launchOrigin,
                TargetId: requestedTargetId,
                Model: NormalizeOptional(body.Model),
                Variant: NormalizeOptional(body.Variant),
                WorkspaceRepositories: workspaceRepositories);

            // Resume before any determinable validation. A changed malformed
            // request under an existing key must be a fingerprint conflict,
            // not a new validation response.
            try
            {
                var resumed = await launcher.ResumeIdempotentAsync(
                    project.Id,
                    idempotencyKey,
                    launchRequest,
                    ct);
                if (resumed is not null)
                {
                    return AgentSessionLaunchRoutes.AcceptedLaunch(
                        project.Id,
                        project.Name,
                        resumed,
                        resumed.WorkspaceName ?? workspaceName,
                        resumed.Origin ?? launchOrigin,
                        resumed.TargetId ?? resumed.AgentId,
                        attachmentResults: resumed.AttachmentResults);
                }
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
                return AgentSessionLaunchRoutes.LaunchSetupPending(ex);
            }
            catch (AgentReadinessException ex)
            {
                return AgentSessionLaunchRoutes.ReadinessRejected(ex);
            }

            var hintError = ValidateHints(body);
            if (hintError is not null)
                return ApiResults.BadRequest(hintError.Value.Message, "validation_failed", new { fields = new[] { hintError.Value.Field } });

            var hasText = !string.IsNullOrWhiteSpace(body.Prompt);
            if (!hasText && body.Attachments is not { Count: > 0 })
            {
                return ApiResults.BadRequest(
                    "input requires non-empty prompt or at least one attachment",
                    "input_required",
                    new { fields = new[] { "prompt", "attachments" } });
            }

            var contextError = await AgentSessionLaunchRoutes.ValidateContextAsync(
                body.Context,
                project.Id,
                issueQuerier,
                epicQuerier,
                grains);
            if (contextError is not null)
                return contextError;

            AgentInputAttachmentAcceptanceBatch attachmentBatch;
            try
            {
                attachmentBatch = await attachments.ValidateAndBindAgentInputAsync(
                    project.Id,
                    preMintedSessionId,
                    preMintedInputId,
                    body.Attachments,
                    ct);
            }
            catch (AttachmentLimitException ex)
            {
                return ApiResults.BadRequest(ex.Message, "attachment_limit_exceeded", new { fields = new[] { "attachments" } });
            }
            catch (AttachmentValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "attachment_invalid", new { fields = new[] { "attachments" } });
            }

            if (attachmentBatch.AcceptedCount == 0 && !hasText)
            {
                return ApiResults.BadRequest(
                    "input has no usable content: all attachments were rejected",
                    "input_unusable",
                    new
                    {
                        fields = new[] { "prompt", "attachments" },
                        attachments = attachmentBatch.Results
                            .Select(AgentSessionLaunchRoutes.BuildAttachmentResultDto)
                            .ToArray(),
                    });
            }

            var newlyBoundIds = attachmentBatch.NewlyBoundAttachmentIds ?? [];
            async Task RollbackAttachmentsAsync() => await attachments.UnbindAgentInputAsync(
                project.Id,
                preMintedSessionId,
                preMintedInputId,
                newlyBoundIds,
                CancellationToken.None);

            var callerHint = new ExecutionConfigHint(
                NormalizeOptional(body.Runtime),
                NormalizeOptional(body.Model),
                NormalizeOptional(body.Variant));
            var agentGrain = grains.GetGrain<IAgentGrain>(GrainKey.Agent(project.Id, preMintedAgentId));
            var adopted = await agentGrain.ShowAsync();
            AgentInfo agent;

            if (adopted is not null)
            {
                // A process crash after Agent creation but before the
                // coordinator plan is persisted leaves this deterministic
                // grain behind. It is the only Agent this key may adopt.
                if (adopted.Status == AgentStatus.Archived)
                {
                    await RollbackAttachmentsAsync();
                    return ApiResults.Conflict(
                        "The deterministic Agent for this Idempotency-Key is archived and cannot be adopted.",
                        "AGENT_NAME_CONFLICT",
                        new { name = adopted.Name });
                }

                AgentTaskDefinition adoptedDefinition;
                try
                {
                    adoptedDefinition = await definitions.CreateAsync(
                        project.Id,
                        body.Prompt,
                        attachmentBatch.AcceptedCount > 0,
                        NormalizeOptional(body.Name),
                        callerHint,
                        idempotencyKey,
                        ct,
                        occupiedNameToIgnore: adopted.Name);
                }
                catch (AgentTaskDefinitionExecutionConfigException ex)
                {
                    await RollbackAttachmentsAsync();
                    return ApiResults.Conflict(
                        ex.Message,
                        "execution_config_unresolvable",
                        new
                        {
                            repairs = new[]
                            {
                                "supply runtime/model/variant hints",
                                "configure the Project default execution configuration",
                            },
                        });
                }

                if (!DefinitionMatches(adopted, adoptedDefinition))
                {
                    await RollbackAttachmentsAsync();
                    return ApiResults.Conflict(
                        "The Idempotency-Key identifies a different task-first definition.",
                        "launch_idempotency_conflict",
                        new { idempotencyKey });
                }

                agent = adopted;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(body.Name)
                    && (BuiltInAgentCatalog.IsReservedName(body.Name)
                        || await agentQuerier.GetByNameAsync(project.Id, body.Name.Trim()) is not null))
                {
                    await RollbackAttachmentsAsync();
                    return ApiResults.Conflict(
                        $"Agent name '{body.Name.Trim()}' is already used",
                        "AGENT_NAME_CONFLICT",
                        new { name = body.Name.Trim() });
                }

                agent = null!;
                for (var attempt = 0; attempt < MaxNameRaceRetries; attempt++)
                {
                    AgentTaskDefinition definition;
                    try
                    {
                            definition = await definitions.CreateAsync(
                            project.Id,
                            body.Prompt,
                            attachmentBatch.AcceptedCount > 0,
                            NormalizeOptional(body.Name),
                            callerHint,
                            idempotencyKey,
                            ct);
                    }
                    catch (AgentTaskDefinitionExecutionConfigException ex)
                    {
                        await RollbackAttachmentsAsync();
                        return ApiResults.Conflict(
                            ex.Message,
                            "execution_config_unresolvable",
                            new
                            {
                                repairs = new[]
                                {
                                    "supply runtime/model/variant hints",
                                    "configure the Project default execution configuration",
                                },
                            });
                    }

                    try
                    {
                        agent = await agentGrain.CreateAsync(new AgentCreateData(
                            project.Id,
                            definition.Name,
                            definition.Description,
                            definition.Instructions,
                            definition.AgentConfig,
                            Skills: [],
                            MaxConcurrentRuns: null));
                        break;
                    }
                    catch (AgentNameConflictException) when (string.IsNullOrWhiteSpace(body.Name) && attempt + 1 < MaxNameRaceRetries)
                    {
                        // Re-list names and let the factory choose the next
                        // ordinal before retrying the same deterministic id.
                    }
                    catch (AgentNameConflictException)
                    {
                        await RollbackAttachmentsAsync();
                        return ApiResults.Conflict(
                            "The derived Agent name is still unavailable after bounded retries.",
                            "AGENT_NAME_CONFLICT",
                            new { name = NormalizeOptional(body.Name) });
                    }
                }

                if (agent is null)
                {
                    await RollbackAttachmentsAsync();
                    return ApiResults.Conflict(
                        "The derived Agent name is still unavailable after bounded retries.",
                        "AGENT_NAME_CONFLICT");
                }
            }

            if (!hasExplicitWorkspace)
            {
                workspaceName = launchOrigin == "cli"
                    ? await provisioner.EnsureCliWorkspaceAsync(project.Id, timeProvider.GetUtcNow())
                    : await provisioner.EnsureWebWorkspaceAsync(project.Id, preMintedSessionId, timeProvider.GetUtcNow());
                workspaceRepositories = await ResolveWorkspaceRepositoriesAsync(
                    project,
                    workspaceName,
                    grains);
                launchRequest = launchRequest with
                {
                    WorkspaceName = workspaceName,
                    WorkspaceRepositories = workspaceRepositories,
                };
            }

            var effectiveTargetId = requestedTargetId ?? agent.Id;
            var launchContext = new AgentLaunchContext(
                ProjectId: project.Id,
                IssueNumber: body.Context?.IssueNumber,
                EpicNumber: body.Context?.EpicNumber,
                Repository: body.Context?.Repository,
                WorkspacePath: body.Context?.WorkspacePath,
                WorkspaceName: workspaceName,
                Title: null,
                Origin: launchOrigin,
                TargetId: effectiveTargetId);

            var retainNewlyBoundAttachments = false;
            try
            {
                var result = await launcher.LaunchIdempotentAsync(
                    agent,
                    body.Prompt ?? string.Empty,
                    launchContext,
                    idempotencyKey,
                    launchRequest,
                    attachmentBatch.Results
                        .Where(result => result.IsAccepted && result.Descriptor is not null)
                        .Select(result => result.Descriptor!)
                        .ToArray(),
                    preMintedSessionId,
                    preMintedInputId,
                    preMintedTurnId,
                    ct,
                    attachmentResults: attachmentBatch.Results);
                retainNewlyBoundAttachments = true;
                return AgentSessionLaunchRoutes.AcceptedLaunch(
                    project.Id,
                    project.Name,
                    result,
                    workspaceName,
                    launchOrigin,
                    effectiveTargetId,
                    attachmentBatch.Results);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "validation_failed");
            }
            catch (LaunchIdempotencyConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, "launch_idempotency_conflict", new { idempotencyKey = ex.IdempotencyKey });
            }
            catch (LaunchSetupPendingException ex)
            {
                retainNewlyBoundAttachments = true;
                return AgentSessionLaunchRoutes.LaunchSetupPending(ex);
            }
            catch (AgentReadinessException ex)
            {
                return AgentSessionLaunchRoutes.ReadinessRejected(ex);
            }
            finally
            {
                if (!retainNewlyBoundAttachments)
                    await RollbackAttachmentsAsync();
            }
        });

        return app;
    }

    private static (string Field, string Message)? ValidateHints(AgentTaskBody body)
    {
        if (HasNonNullProperty(body.Raw, "runtime")
            && string.IsNullOrWhiteSpace(body.Runtime))
            return ("runtime", "runtime must be one of opencode, pi.");
        if (HasNonNullProperty(body.Raw, "runtime")
            && !AgentConfigSchema.AllowedRuntimes.Contains(body.Runtime!))
            return ("runtime", $"runtime '{body.Runtime}' is not supported; choose opencode or pi.");
        if (HasNonNullProperty(body.Raw, "model")
            && string.IsNullOrWhiteSpace(body.Model))
            return ("model", "model must use the provider/model form.");
        if (HasNonNullProperty(body.Raw, "model")
            && !AgentConfigSchema.HasProviderModelForm(body.Model))
            return ("model", "model must use the provider/model form.");
        if (HasNonNullProperty(body.Raw, "variant")
            && string.IsNullOrWhiteSpace(body.Variant))
            return ("variant", "variant must not be empty.");
        return null;
    }

    private static bool HasNonNullProperty(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty(name, out var property)
        && property.ValueKind != JsonValueKind.Null;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool DefinitionMatches(AgentInfo agent, AgentTaskDefinition expected) =>
        string.Equals(agent.Name, expected.Name, StringComparison.Ordinal)
        && string.Equals(agent.Description, expected.Description, StringComparison.Ordinal)
        && string.Equals(agent.Instructions, expected.Instructions, StringComparison.Ordinal)
        && string.Equals(
            agent.AgentConfig?.GetRawText(),
            expected.AgentConfig.GetRawText(),
            StringComparison.Ordinal);

    private static async Task<IReadOnlyList<WorkspaceRepositorySnapshot>?> ResolveWorkspaceRepositoriesAsync(
        ProjectInfo project,
        string? workspaceName,
        IGrainFactory grains)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
            return null;

        var workspace = await grains.GetGrain<Mohist.Server.Workspace.Grains.IWorkspaceGrain>(
            GrainKey.Workspace(project.Id, workspaceName)).GetAsync();
        if (workspace is null || workspace.RepositoryNames.Count == 0)
            return null;

        return workspace.RepositoryNames
            .Select(name => project.Repositories.FirstOrDefault(repository =>
                string.Equals(repository.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(repository => repository is not null)
            .Select(repository => new WorkspaceRepositorySnapshot(
                repository!.Name,
                repository.GitUrl,
                repository.ResolvedBaseBranch))
            .ToArray();
    }
}

/// <summary>
/// Raw-JSON presence-bound request for the task-first route. The raw
/// property set is kept alongside typed values so validation can distinguish
/// an omitted hint from an explicitly empty hint.
/// </summary>
public sealed record AgentTaskBody(
    string? Prompt,
    AgentSessionLaunchContextRef? Context,
    IReadOnlyList<string>? Attachments,
    string? Name,
    string? Runtime,
    string? Model,
    string? Variant,
    IReadOnlyList<string> UndeclaredFields,
    JsonElement Raw,
    string? BindingError = null)
{
    public static async ValueTask<AgentTaskBody?> BindAsync(HttpContext context)
    {
        try
        {
            var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
            if (raw.ValueKind != JsonValueKind.Object)
            {
                return new AgentTaskBody(
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    raw,
                    "body must be a JSON object");
            }

            var undeclared = raw.EnumerateObject()
                .Where(property => !AgentTaskRoutes.AllowedTopLevelFields.Contains(property.Name))
                .Select(property => property.Name)
                .ToArray();
            AgentSessionLaunchContextRef? requestContext = null;
            if (raw.TryGetProperty("context", out var contextElement)
                && contextElement.ValueKind != JsonValueKind.Null)
            {
                if (contextElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("context must be an object");
                requestContext = AgentSessionLaunchBody.ReadContext(contextElement);
            }

            return new AgentTaskBody(
                StringValue(raw, "prompt"),
                requestContext,
                AgentSessionLaunchBody.ReadAttachments(raw),
                StringValue(raw, "name"),
                StringValue(raw, "runtime"),
                StringValue(raw, "model"),
                StringValue(raw, "variant"),
                undeclared,
                raw);
        }
        catch (JsonException ex)
        {
            return new AgentTaskBody(null, null, null, null, null, null, null, [], default, ex.Message);
        }
    }

    private static string? StringValue(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"{name} must be a string");
        return value.GetString();
    }
}
