using Microsoft.AspNetCore.Http;
using Mohist.Server.Issue.Grains.Coordinator;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using System.Text.Json;
using RepositoryPolicy = Mohist.Server.Project.Domain.RepositoryPolicy;

namespace Mohist.Server.Api;

public static class ProjectRoutes
{
    public static WebApplication MapProjectRoutes(this WebApplication app)
    {
        var root = app.MapGroup("/api/projects");

        root.MapGet("/", async (ProjectQuerier projectsQuery) =>
        {
            var projects = await projectsQuery.ListAllAsync();
            return ApiResults.Ok(projects);
        });

        root.MapPost("/", async (CreateProjectRequest req, IGrainFactory grains, ProjectQuerier projectsQuery) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return ApiResults.BadRequest("name is required");

            if (!ProjectName.TryNormalize(req.Name, out var projectName, out var nameError))
                return ApiResults.BadRequest(nameError!, "invalid_project_name");

            if (req.Repository is null)
                return ApiResults.BadRequest("repository is required", "repository_required");

            if (TryGetForbiddenLocalRepositoryField(
                    req.Repository.Path,
                    req.Repository.Remote,
                    req.Repository.ResolvedPath,
                    out var forbiddenField))
            {
                return ApiResults.BadRequest(
                    $"repository.{forbiddenField} is not accepted; repositories declare Git addresses only",
                    "repository_local_field_forbidden");
            }

            if (IsSupplied(req.Repository.IsDefault))
                return ApiResults.BadRequest("repository.isDefault is derived by the server", "repository_initial_default_forbidden");

            if (IsSupplied(req.Repository.SetDefault))
                return ApiResults.BadRequest("repository.setDefault is not accepted during project creation", "repository_initial_default_forbidden");

            if (string.IsNullOrWhiteSpace(req.Repository.Name))
                return ApiResults.BadRequest("repository.name is required", "repository_name_required");

            if (string.IsNullOrWhiteSpace(req.Repository.GitUrl))
                return ApiResults.BadRequest("repository.gitUrl is required", "repository_giturl_required");

            if (await projectsQuery.ExistsAsync(projectName))
                return ApiResults.Conflict($"Project '{projectName}' already exists");

            var id = $"proj_{Guid.NewGuid():N}";
            var projectGrain = grains.GetGrain<IProjectGrain>(id);

            var initial = new RepositoryInfo
            {
                Name = req.Repository.Name,
                GitUrl = req.Repository.GitUrl,
                BaseBranch = string.IsNullOrWhiteSpace(req.Repository.BaseBranch)
                    ? RepositoryPolicy.DefaultBaseBranch
                    : req.Repository.BaseBranch,
                IsDefault = true,
            };

            try
            {
                var project = await projectGrain.CreateAsync(projectName, initial);
                return Results.Json(new { success = true, data = project }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_initial_repository");
            }
        });

        // -------------------------------------------------------------------
        // /api/projects/{projectRef}/... — filter resolves {projectRef} for us
        // -------------------------------------------------------------------
        var byRef = root.MapGroup("/{projectRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        byRef.MapGet("", async (HttpContext context) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(project);
        });

        byRef.MapGet("/actions", async (IActionCatalogSource catalogSource) =>
        {
            var catalog = await catalogSource.GetCatalogAsync();
            return ApiResults.Ok(catalog ?? new ActionCatalog([], []));
        });

        byRef.MapGet("/workflow-profiles", async (HttpContext context, IWorkflowProfileProvider provider) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(await provider.ListAsync(project.Id));
        });

        byRef.MapPost("/workflow-profiles", async (
            HttpContext context,
            WorkflowProfileSaveRequest request,
            IWorkflowProfileProvider provider) =>
        {
            var project = context.GetResolvedProject();
            try
            {
                var result = await provider.CreateAsync(project.Id, request.ToEntry(project.Id));
                return result.ValidationResult.IsValid
                    ? Results.Json(new { success = true, data = result.Profile, validation = result.ValidationResult }, statusCode: 201)
                    : ApiResults.BadRequest("WorkflowProfile validation failed", "workflow_profile_validation", result.ValidationResult);
            }
            catch (WorkflowProfileReadOnlyException ex)
            {
                return ApiResults.Conflict(ex.Message, "workflow_profile_read_only");
            }
            catch (WorkflowDefinitionValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "workflow_profile_definition_validation", ex.Errors);
            }
        });

        byRef.MapGet("/workflow-profiles/{*profileId}", async (
            HttpContext context,
            string profileId,
            IWorkflowProfileProvider provider) =>
        {
            var project = context.GetResolvedProject();
            var id = Uri.UnescapeDataString(profileId);
            var profile = await provider.GetAsync(project.Id, id);
            if (profile is null)
                return ApiResults.NotFound($"WorkflowProfile '{id}' was not found");

            var definition = await provider.GetDefinitionAsync(project.Id, id);
            if (definition is null)
                return ApiResults.NotFound($"WorkflowProfile '{id}' has no readable definition");

            return ApiResults.Ok(new WorkflowProfileDetailResponse(
                profile.ProjectId,
                profile.ProfileId,
                profile.Name,
                profile.Description,
                profile.SourceProvenance,
                profile.IsBuiltIn,
                profile.DefinitionSource,
                profile.AgentAction,
                profile.AgentRuntime,
                definition.Stages
                    .Select(stage => new WorkflowProfileStageSummary(
                        stage.Stage,
                        stage.RequiresApproval,
                        stage.Tasks.Select(task => task.Id).ToArray(),
                        stage.Checks.Select(check => check.Id).ToArray()))
                    .ToArray()));
        });

        byRef.MapPatch("/workflow-profiles/{*profileId}", async (
            HttpContext context,
            string profileId,
            WorkflowProfileAgentActionRequest request,
            IGrainFactory grains,
            IWorkflowProfileProvider provider) =>
        {
            var project = context.GetResolvedProject();
            var id = Uri.UnescapeDataString(profileId);
            try
            {
                var result = await grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(project.Id)
                    .SetAgentActionOverrideAsync(
                        new WorkflowProfileCommandPayload.SetAgentActionOverride(
                            project.Id,
                            id,
                            string.IsNullOrWhiteSpace(request.AgentAction) ? null : request.AgentAction.Trim()),
                        $"api-agent-action:{Guid.NewGuid():N}",
                        expectedRevision: null);
                if (!result.IsApplied)
                    return result.Code == WorkflowProfileReferenceResultCode.ProfileUnknown
                        ? ApiResults.NotFound(result.Message ?? $"WorkflowProfile '{id}' was not found")
                        : ApiResults.Conflict(result.Message ?? "Unable to update Agent Action", "workflow_profile_agent_action_conflict");

                var profile = await provider.GetAsync(project.Id, id);
                return profile is null
                    ? ApiResults.NotFound($"WorkflowProfile '{id}' was not found")
                    : ApiResults.Ok(profile);
            }
            catch (WorkflowProfileNotFoundException ex)
            {
                return ApiResults.NotFound(ex.Message);
            }
            catch (WorkflowDefinitionValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "workflow_profile_agent_action_validation", ex.Errors);
            }
        });

        byRef.MapPut("/workflow-profiles/{*profileId}", async (
            HttpContext context,
            string profileId,
            WorkflowProfileSaveRequest request,
            IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();
            var id = Uri.UnescapeDataString(profileId);
            try
            {
                var result = await grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(project.Id)
                    .UpdateProfileAsync(
                        new WorkflowProfileCommandPayload.UpdateProfile(
                            project.Id,
                            id,
                            request.Name ?? id,
                            request.Description ?? string.Empty,
                            request.DefinitionSource),
                        $"api-profile-update:{Guid.NewGuid():N}",
                        expectedRevision: null);
                return result.ValidationResult.IsValid
                    ? Results.Json(new { success = true, data = result.Profile, validation = result.ValidationResult })
                    : ApiResults.BadRequest("WorkflowProfile validation failed", "workflow_profile_validation", result.ValidationResult);
            }
            catch (WorkflowProfileReadOnlyException ex)
            {
                return ApiResults.Conflict(ex.Message, "workflow_profile_read_only");
            }
            catch (WorkflowProfileNotFoundException ex)
            {
                return ApiResults.NotFound(ex.Message);
            }
            catch (WorkflowDefinitionValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "workflow_profile_definition_validation", ex.Errors);
            }
        });

        byRef.MapDelete("/workflow-profiles/{*profileId}", async (
            HttpContext context,
            string profileId,
            IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();
            var id = Uri.UnescapeDataString(profileId);
            var result = await grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(project.Id)
                .DeleteProfileAsync(
                    new WorkflowProfileCommandPayload.DeleteProfile(project.Id, id),
                    $"api-delete:{Guid.NewGuid():N}",
                    null);
            return result.Code switch
            {
                WorkflowProfileReferenceResultCode.Applied or WorkflowProfileReferenceResultCode.AlreadyApplied =>
                    ApiResults.Ok(new { deleted = true, profileId = id }),
                WorkflowProfileReferenceResultCode.ProfileReadOnly => ApiResults.Conflict(result.Message ?? "WorkflowProfile is read-only", "workflow_profile_read_only"),
                WorkflowProfileReferenceResultCode.BlockedByReferences => ApiResults.Conflict(result.Message ?? "WorkflowProfile is still referenced", "workflow_profile_referenced"),
                _ => ApiResults.NotFound(result.Message ?? $"WorkflowProfile '{id}' was not found"),
            };
        });

        byRef.MapGet("/workflow-profile/default", async (
            HttpContext context,
            IWorkflowProfileProvider provider) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(new
            {
                projectId = project.Id,
                defaultWorkflowProfileId = await provider.GetDefaultProfileIdAsync(project.Id),
                disabledWorkflowProfileIds = (await provider.GetDisabledProfileIdsAsync(project.Id))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            });
        });

        byRef.MapPut("/workflow-profile/default", async (
            HttpContext context,
            SetDefaultWorkflowProfileRequest request,
            IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return ApiResults.BadRequest("profileId is required", "workflow_profile_required");

            var project = context.GetResolvedProject();
            var result = await grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(project.Id)
                .SetProjectDefaultAsync(
                    new WorkflowProfileCommandPayload.SetProjectDefault(project.Id, request.ProfileId),
                    $"api-default:{Guid.NewGuid():N}",
                    null);
            return result.Code switch
            {
                WorkflowProfileReferenceResultCode.Applied or WorkflowProfileReferenceResultCode.AlreadyApplied =>
                    ApiResults.Ok(new ProjectWorkflowProfileResponse(project.Id, request.ProfileId)),
                WorkflowProfileReferenceResultCode.ProfileUnknown =>
                    ApiResults.NotFound(result.Message ?? $"WorkflowProfile '{request.ProfileId}' was not found"),
                _ => ApiResults.Conflict(result.Message ?? "WorkflowProfile selection rejected", "workflow_profile_selection_rejected"),
            };
        });

        byRef.MapPost("/workflow-profile/disable", async (
            HttpContext context,
            ToggleWorkflowProfileRequest request,
            IWorkflowProfileProvider provider) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return ApiResults.BadRequest("profileId is required", "workflow_profile_required");

            var project = context.GetResolvedProject();
            try
            {
                await provider.SetProfileEnabledAsync(project.Id, request.ProfileId, enabled: false);
                return ApiResults.Ok(new { profileId = request.ProfileId, enabled = false });
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "unknown_workflow_profile");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "last_enabled_workflow_profile");
            }
        });

        byRef.MapPost("/workflow-profile/enable", async (
            HttpContext context,
            ToggleWorkflowProfileRequest request,
            IWorkflowProfileProvider provider) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
                return ApiResults.BadRequest("profileId is required", "workflow_profile_required");

            var project = context.GetResolvedProject();
            try
            {
                await provider.SetProfileEnabledAsync(project.Id, request.ProfileId, enabled: true);
                return ApiResults.Ok(new { profileId = request.ProfileId, enabled = true });
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "unknown_workflow_profile");
            }
        });

        byRef.MapPost("/use", async (HttpContext context) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(project);
        });

        byRef.MapPatch("", async (HttpContext context, IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();
            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            var updated = await projectGrain.UpdateAsync();
            return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound("Project not found");
        });

        byRef.MapDelete("", async (HttpContext context, IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();
            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            await projectGrain.DeleteAsync();
            return ApiResults.Ok();
        });

        byRef.MapGet("/repositories", async (HttpContext context) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(project.Repositories);
        });

        byRef.MapPost("/repositories", async (HttpContext context, AddRepositoryRequest req, IGrainFactory grains) =>
        {
            if (TryGetForbiddenLocalRepositoryField(req.Path, req.Remote, req.ResolvedPath, out var forbiddenField))
            {
                return ApiResults.BadRequest(
                    $"{forbiddenField} is not accepted; repositories declare Git addresses only",
                    "repository_local_field_forbidden");
            }

            if (IsSupplied(req.IsDefault))
                return ApiResults.BadRequest("isDefault is derived by the server; use setDefault instead", "repository_default_forbidden");

            if (IsSupplied(req.SetDefault) && req.SetDefault.ValueKind != JsonValueKind.True)
                return ApiResults.BadRequest("setDefault must be true when supplied", "repository_default_selection_invalid");

            if (string.IsNullOrWhiteSpace(req.Name))
                return ApiResults.BadRequest("name is required", "repository_name_required");
            if (string.IsNullOrWhiteSpace(req.GitUrl))
                return ApiResults.BadRequest("gitUrl is required", "repository_giturl_required");

            var project = context.GetResolvedProject();
            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            try
            {
                var updated = await projectGrain.AddRepositoryAsync(
                    req.Name,
                    req.GitUrl,
                    req.BaseBranch,
                    req.SetDefault.ValueKind == JsonValueKind.True);
                return updated is not null
                    ? Results.Json(new { success = true, data = updated }, statusCode: 201)
                    : ApiResults.NotFound("Project not found");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
            catch (ArgumentException ex)
            {
                if (TryGetRepositoryNameError(ex, req.Name, out var conflictMessage))
                    return ApiResults.Conflict(conflictMessage!, "repository_name_conflict");
                if (TryGetRepositoryAliasError(ex, out var aliasMessage))
                    return ApiResults.Conflict(aliasMessage!, "repository_alias_conflict");
                return ApiResults.BadRequest(ex.Message);
            }
        });

        byRef.MapPatch("/repositories/{repoName}", async (HttpContext context, string repoName, UpdateRepositoryRequest req, IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();
            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);

            if (TryGetForbiddenLocalRepositoryField(req.Path, req.Remote, req.ResolvedPath, out var forbiddenField))
            {
                return ApiResults.BadRequest(
                    $"{forbiddenField} is not accepted; repositories declare Git addresses only",
                    "repository_local_field_forbidden");
            }

            if (IsSupplied(req.NewName))
                return ApiResults.BadRequest("Repository names are immutable", "repository_name_immutable");

            if (IsSupplied(req.IsDefault))
                return ApiResults.BadRequest("isDefault is derived by the server; use setDefault instead", "repository_default_forbidden");

            if (IsSupplied(req.SetDefault) && req.SetDefault.ValueKind != JsonValueKind.True)
                return ApiResults.BadRequest("setDefault must be true when supplied", "repository_default_selection_invalid");

            if (req.GitUrl is not null && string.IsNullOrWhiteSpace(req.GitUrl))
                return ApiResults.BadRequest("gitUrl must be a non-empty string", "repository_giturl_required");

            var setDefault = req.SetDefault.ValueKind == JsonValueKind.True;
            var hasMetadataUpdate = req.GitUrl is not null || req.BaseBranch is not null;

            if (setDefault && hasMetadataUpdate)
                return ApiResults.BadRequest(
                    "setDefault cannot be combined with repository metadata updates",
                    "repository_patch_mixed_scope");

            if (setDefault)
            {
                try
                {
                    var updated = await projectGrain.SetDefaultRepositoryAsync(repoName);
                    return updated is not null
                        ? ApiResults.Ok(updated)
                        : ApiResults.NotFound($"Repository '{repoName}' not found in project '{project.Id}'");
                }
                catch (ArgumentException ex)
                {
                    return ApiResults.BadRequest(ex.Message);
                }
            }

            if (!hasMetadataUpdate)
                return ApiResults.BadRequest(
                    "Provide gitUrl and/or baseBranch to update repository metadata",
                    "repository_update_empty");

            var coordinator = grains.GetGrain<IIssueRepositoryCoordinatorGrain>(project.Id);
            var result = await coordinator.UpdateRepositoryAsync(
                new RepositoryCommandPayload.Update(project.Id, repoName, req.GitUrl, req.BaseBranch),
                commandId: $"update:{project.Id}:{repoName}:{Guid.NewGuid():N}",
                expectedRevision: null);

            switch (result.Code)
            {
                case IssueRepositoryBindingResultCode.Applied:
                case IssueRepositoryBindingResultCode.AlreadyApplied:
                {
                    var updated = await projectGrain.GetAsync();
                    return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound($"Project '{project.Id}' not found");
                }
                case IssueRepositoryBindingResultCode.RepositoryInUse:
                    return ApiResults.Conflict(
                        result.Message ?? $"Repository '{repoName}' is referenced by one or more non-terminal issues",
                        "repository_in_use");
                case IssueRepositoryBindingResultCode.RepositoryNotFound:
                    return ApiResults.NotFound(result.Message ?? $"Repository '{repoName}' not found in project '{project.Id}'");
                case IssueRepositoryBindingResultCode.RepositoryInvalid:
                    if (result.Message is not null && result.Message.Contains("shares its Git remote", StringComparison.OrdinalIgnoreCase))
                        return ApiResults.Conflict(result.Message, "repository_alias_conflict");
                    return ApiResults.BadRequest(result.Message ?? "Repository metadata is invalid");
                case IssueRepositoryBindingResultCode.RepositoryStaleRevision:
                    return ApiResults.Conflict(result.Message ?? "Repository revision is stale", "repository_stale_revision");
                default:
                    return ApiResults.Conflict(result.Message ?? "Repository update rejected");
            }
        });

        byRef.MapDelete("/repositories/{repoName}", async (HttpContext context, string repoName, IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();

            // deletion enters through the
            // Project-scoped coordinator. The coordinator performs the
            // committed-state blocker check before fencing; the
            // Project participant still owns the existence / default
            // precedence check so the existing not-found / default
            // envelope semantics are preserved.
            var coordinator = grains.GetGrain<IIssueRepositoryCoordinatorGrain>(project.Id);
            var coordinatorResult = await coordinator.RemoveRepositoryAsync(
                new RepositoryCommandPayload.Remove(
                    ProjectId: project.Id,
                    RepositoryName: repoName),
                commandId: $"remove:{project.Id}:{repoName}:{Guid.NewGuid():N}",
                expectedRevision: null);

            switch (coordinatorResult.Code)
            {
                case IssueRepositoryBindingResultCode.Applied:
                case IssueRepositoryBindingResultCode.AlreadyApplied:
                {
                    var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
                    var updated = await projectGrain.GetAsync();
                    return updated is not null
                        ? ApiResults.Ok(updated)
                        : ApiResults.NotFound($"Project '{project.Id}' not found");
                }
                case IssueRepositoryBindingResultCode.RepositoryInUse:
                    return ApiResults.Conflict(
                        coordinatorResult.Message ?? $"Repository '{repoName}' is referenced by one or more non-terminal issues",
                        "repository_in_use");
                case IssueRepositoryBindingResultCode.RepositoryNotFound:
                    return ApiResults.NotFound(coordinatorResult.Message ?? $"Repository '{repoName}' not found in project '{project.Id}'");
                case IssueRepositoryBindingResultCode.RepositoryDefault:
                    return ApiResults.Conflict(
                        coordinatorResult.Message ?? "Cannot delete the default repository",
                        "repository_default_deletion_conflict");
                case IssueRepositoryBindingResultCode.RepositoryStaleRevision:
                    return ApiResults.Conflict(
                        coordinatorResult.Message ?? "Repository revision is stale",
                        "repository_stale_revision");
                default:
                    return ApiResults.Conflict(coordinatorResult.Message ?? "Repository deletion rejected");
            }
        });

        // Replace-on-set write surface for the Project default execution
        // configuration. PUT and PATCH share one closed field set (runtime,
        // model, variant); a success replaces any prior default and returns
        // the updated Project (the read surface is GET /{projectRef}).
        byRef.MapPut("/default-execution-config", async (
            HttpContext context,
            ProjectDefaultExecutionConfigBody? body,
            IGrainFactory grains) =>
        {
            var rejection = await SetDefaultExecutionConfigAsync(context, body, grains);
            return rejection ?? Results.Ok(
                await grains.GetGrain<IProjectGrain>(context.GetResolvedProject().Id).GetAsync());
        });

        byRef.MapPatch("/default-execution-config", async (
            HttpContext context,
            ProjectDefaultExecutionConfigBody? body,
            IGrainFactory grains) =>
        {
            var rejection = await SetDefaultExecutionConfigAsync(context, body, grains);
            return rejection ?? Results.Ok(
                await grains.GetGrain<IProjectGrain>(context.GetResolvedProject().Id).GetAsync());
        });

        byRef.MapGet("/variables", async (HttpContext context, ProjectVariableStore variableStore) =>
        {
            var project = context.GetResolvedProject();
            var variables = await variableStore.GetVariablesAsync(project.Id);
            return ApiResults.Ok(variables);
        });

        byRef.MapPut("/variables", async (HttpContext context, VariableBundle bundle, ProjectVariableStore variableStore) =>
        {
            var project = context.GetResolvedProject();
            try
            {
                return ApiResults.Ok(await variableStore.SetVariablesAsync(project.Id, bundle));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_variables");
            }
        });

        byRef.MapPatch("/variables", async (HttpContext context, VariableBundle patch, ProjectVariableStore variableStore) =>
        {
            var project = context.GetResolvedProject();
            try
            {
                return ApiResults.Ok(await variableStore.PatchVariablesAsync(project.Id, patch));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_variables");
            }
        });

        byRef.MapGet("/workflow-profile/prompts", async (HttpContext context, ProjectPromptStore promptStore) =>
        {
            var project = context.GetResolvedProject();
            var prompts = await promptStore.ListPromptsAsync(project.Id);
            return ApiResults.Ok(prompts);
        });

        byRef.MapGet("/workflow-profile/prompts/{key}", async (HttpContext context, string key, ProjectPromptStore promptStore) =>
        {
            var project = context.GetResolvedProject();
            var prompt = await promptStore.GetPromptAsync(project.Id, key);
            return prompt is null
                ? ApiResults.NotFound($"Prompt '{key}' not found")
                : ApiResults.Ok(prompt);
        });

        byRef.MapPut("/workflow-profile/prompts/{key}", async (HttpContext context, string key, PromptUpsertRequest? req, ProjectPromptStore promptStore) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Body))
                return ApiResults.BadRequest("body is required");

            var project = context.GetResolvedProject();
            await promptStore.SetPromptAsync(project.Id, key, req.Body);
            return ApiResults.Ok(new { key, body = req.Body });
        });

        byRef.MapDelete("/workflow-profile/prompts/{key}", async (HttpContext context, string key, ProjectPromptStore promptStore) =>
        {
            var project = context.GetResolvedProject();
            await promptStore.DeletePromptAsync(project.Id, key);
            return ApiResults.Ok();
        });

        byRef.MapPost("/workflow-profile/prompts/{key}/preview", async (HttpContext context, string key, PromptPreviewRequest? req, ProjectPromptStore promptStore) =>
        {
            JsonElement variables;
            if (req?.Variables is { } raw)
                variables = raw;
            else
            {
                using var doc = JsonDocument.Parse("{}");
                variables = doc.RootElement.Clone();
            }

            try
            {
                var project = context.GetResolvedProject();
                var result = await promptStore.PreviewPromptAsync(project.Id, key, variables);
                return ApiResults.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.NotFound(ex.Message);
            }
        });

        return app;
    }

    private static EffectivePrompt ToTemplateRoutePrompt(EffectivePrompt prompt) =>
        prompt.Source == "project"
            ? prompt with { Source = "project-override" }
            : prompt;

    internal static object ToActionValidationNotice(ActionValidationStatus status) => status switch
    {
        ActionValidationStatus.Performed => new { performed = true },
        ActionValidationStatus.Skipped => new
        {
            performed = false,
            reason = "Action-contract validation was not performed: no Runner has reported an Action catalog yet.",
        },
        _ => new { performed = false },
    };

    private static bool TryGetRepositoryNameError(
        ArgumentException exception,
        string requestedName,
        out string? message)
    {
        message = exception.Message;
        return message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetRepositoryAliasError(
        ArgumentException exception,
        out string? message)
    {
        message = exception.Message;
        return message.Contains("shares its Git remote", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupplied(JsonElement value) =>
        value.ValueKind != JsonValueKind.Undefined;

    private static async Task<IResult?> SetDefaultExecutionConfigAsync(
        HttpContext context,
        ProjectDefaultExecutionConfigBody? body,
        IGrainFactory grains)
    {
        if (body is null)
            return ApiResults.BadRequest("request body is required", "body_required");

        if (body.UndeclaredFields.Count > 0)
        {
            return ApiResults.BadRequest(
                $"unsupported top-level field(s): {string.Join(", ", body.UndeclaredFields)}; " +
                "the default execution configuration accepts only runtime, model, and variant.",
                "unsupported_field",
                new { fields = body.UndeclaredFields.ToArray() });
        }

        try
        {
            var updated = await grains
                .GetGrain<IProjectGrain>(context.GetResolvedProject().Id)
                .SetDefaultExecutionConfigAsync(new ExecutionConfigHint(
                    body.Runtime,
                    body.Model,
                    body.Variant));
            return updated is null ? ApiResults.NotFound("Project not found") : null;
        }
        catch (ArgumentException ex)
        {
            return ApiResults.BadRequest(ex.Message, "invalid_default_execution_config");
        }
    }

    private static bool TryGetForbiddenLocalRepositoryField(
        JsonElement path,
        JsonElement remote,
        JsonElement resolvedPath,
        out string? field)
    {
        if (IsSupplied(path))
        {
            field = "path";
            return true;
        }

        if (IsSupplied(remote))
        {
            field = "remote";
            return true;
        }

        if (IsSupplied(resolvedPath))
        {
            field = "resolvedPath";
            return true;
        }

        field = null;
        return false;
    }
}

public sealed record ProjectWorkflowProfileResponse(string ProjectId, string ProfileId);

public sealed record WorkflowProfileDetailResponse(
    string ProjectId,
    string ProfileId,
    string Name,
    string Description,
    WorkflowProfileSourceProvenance SourceProvenance,
    bool IsBuiltIn,
    string? DefinitionSource,
    string? AgentAction,
    string? AgentRuntime,
    IReadOnlyList<WorkflowProfileStageSummary> Stages);

public sealed record WorkflowProfileStageSummary(
    string Stage,
    bool RequiresApproval,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> Checks);

public sealed record PromptUpsertRequest(string? Body);

public sealed record ProjectPromptOverrideRequest(
    string? DisplayName,
    string? Description,
    string[]? Tags,
    string? Stage,
    string? Body);

public sealed record PromptPreviewRequest(JsonElement? Variables);

public record CreateProjectRequest(string Name, CreateProjectRepositoryRequest? Repository);
public record CreateProjectRepositoryRequest(
    string? Name,
    string? GitUrl,
    string? BaseBranch,
    JsonElement IsDefault = default,
    JsonElement SetDefault = default,
    JsonElement Path = default,
    JsonElement Remote = default,
    JsonElement ResolvedPath = default);
public record UpdateProjectRequest();
public record AddRepositoryRequest(
    string Name,
    string GitUrl,
    string? BaseBranch = null,
    JsonElement SetDefault = default,
    JsonElement IsDefault = default,
    JsonElement Path = default,
    JsonElement Remote = default,
    JsonElement ResolvedPath = default);
public record UpdateRepositoryRequest(
    JsonElement SetDefault = default,
    string? GitUrl = null,
    string? BaseBranch = null,
    JsonElement NewName = default,
    JsonElement IsDefault = default,
    JsonElement Path = default,
    JsonElement Remote = default,
    JsonElement ResolvedPath = default);
public sealed record SetDefaultWorkflowProfileRequest(string ProfileId);
public sealed record ToggleWorkflowProfileRequest(string ProfileId);
public sealed record WorkflowProfileAgentActionRequest(string? AgentAction);

/// <summary>
/// Raw-JSON presence-bound body for
/// <c>PUT/PATCH /api/projects/{projectRef}/default-execution-config</c>.
/// Records every top-level JSON property name so the route can reject
/// undeclared fields before any state changes. The closed set is
/// <c>runtime</c>, <c>model</c>, and optional <c>variant</c>; value rules
/// (runtime ∈ {opencode, pi}, model in <c>provider/model</c> form) are owned
/// by <c>IProjectGrain.SetDefaultExecutionConfigAsync</c>.
/// </summary>
public sealed record ProjectDefaultExecutionConfigBody(
    string? Runtime,
    string? Model,
    string? Variant,
    IReadOnlyList<string> UndeclaredFields)
{
    internal static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "runtime",
        "model",
        "variant",
    };

    public static async ValueTask<ProjectDefaultExecutionConfigBody?> BindAsync(HttpContext context)
    {
        try
        {
            return await BindCoreAsync(context);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async ValueTask<ProjectDefaultExecutionConfigBody> BindCoreAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("the default execution configuration must be a JSON object");

        var undeclared = new List<string>();
        foreach (var property in raw.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name))
                undeclared.Add(property.Name);
        }

        return new ProjectDefaultExecutionConfigBody(
            Runtime: ReadString(raw, "runtime"),
            Model: ReadString(raw, "model"),
            Variant: ReadString(raw, "variant"),
            UndeclaredFields: undeclared);
    }

    private static string? ReadString(JsonElement raw, string name)
    {
        if (!raw.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"{name} must be a string");
        return value.GetString();
    }
}

public sealed record WorkflowProfileSaveRequest(
    string ProfileId,
    string? Name,
    string? Description,
    string DefinitionSource)
{
    public WorkflowProfileCollectionEntry ToEntry(string projectId, string? profileId = null) =>
        new(
            projectId,
            profileId ?? ProfileId,
            Name ?? profileId ?? ProfileId,
            Description ?? string.Empty,
            WorkflowProfileSourceProvenance.Verbatim,
            false,
            DefinitionSource);
}
