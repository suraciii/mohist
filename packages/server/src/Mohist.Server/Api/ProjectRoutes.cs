using Microsoft.AspNetCore.Http;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Domain;
using Mohist.Server.Workflow.Domain;
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

            if (req.Repository.IsDefault is not null)
                return ApiResults.BadRequest("repository.isDefault is derived by the server", "repository_initial_default_forbidden");

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
            if (req.IsDefault is not null)
                return ApiResults.BadRequest("isDefault is derived by the server; use setDefault instead", "repository_default_forbidden");

            if (string.IsNullOrWhiteSpace(req.Name))
                return ApiResults.BadRequest("name is required", "repository_name_required");
            if (string.IsNullOrWhiteSpace(req.GitUrl))
                return ApiResults.BadRequest("gitUrl is required", "repository_giturl_required");

            var project = context.GetResolvedProject();
            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            try
            {
                var updated = await projectGrain.AddRepositoryAsync(req.Name, req.GitUrl, req.BaseBranch, req.SetDefault);
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
                return ApiResults.BadRequest(ex.Message);
            }
        });

        byRef.MapPatch("/repositories/{repoName}", async (HttpContext context, string repoName, UpdateRepositoryRequest req, IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();
            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);

            if (req.NewName is not null)
                return ApiResults.BadRequest("Repository names are immutable", "repository_name_immutable");

            if (req.IsDefault is not null)
                return ApiResults.BadRequest("isDefault is derived by the server; use setDefault instead", "repository_default_forbidden");

            if (req.SetDefault == false)
                return ApiResults.BadRequest("setDefault must be true when supplied", "repository_default_selection_invalid");

            if (req.GitUrl is not null && string.IsNullOrWhiteSpace(req.GitUrl))
                return ApiResults.BadRequest("gitUrl must be a non-empty string", "repository_giturl_required");

            var setDefault = req.SetDefault == true;
            var hasMetadataUpdate = req.GitUrl is not null || req.BaseBranch is not null;

            if (setDefault && hasMetadataUpdate)
                return ApiResults.BadRequest(
                    "setDefault cannot be combined with repository metadata updates",
                    "repository_patch_mixed_scope");

            if (setDefault)
            {
                var updated = await projectGrain.SetDefaultRepositoryAsync(repoName);
                return updated is not null
                    ? ApiResults.Ok(updated)
                    : ApiResults.NotFound($"Repository '{repoName}' not found in project '{project.Id}'");
            }

            if (!hasMetadataUpdate)
                return ApiResults.BadRequest(
                    "Provide gitUrl and/or baseBranch to update repository metadata",
                    "repository_update_empty");

            try
            {
                var updated = await projectGrain.UpdateRepositoryAsync(
                    repoName,
                    gitUrl: req.GitUrl,
                    baseBranch: req.BaseBranch);
                return updated is not null
                    ? ApiResults.Ok(updated)
                    : ApiResults.NotFound($"Repository '{repoName}' not found in project '{project.Id}'");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        byRef.MapDelete("/repositories/{repoName}", async (HttpContext context, string repoName, IGrainFactory grains) =>
        {
            var project = context.GetResolvedProject();
            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            try
            {
                var updated = await projectGrain.RemoveRepositoryAsync(repoName);
                return updated is not null
                    ? ApiResults.Ok(updated)
                    : ApiResults.NotFound($"Repository '{repoName}' not found in project '{project.Id}'");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message, "repository_default_deletion_conflict");
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        // =======================================================================
        // Project workflow templates CRUD
        // =======================================================================

        byRef.MapGet("/workflow-templates", async (HttpContext context, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var templates = await manager.ListTemplatesAsync(project.Id);
            return ApiResults.Ok(templates);
        });

        byRef.MapPost("/workflow-templates", async (HttpContext context, CreateProjectTemplateRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Yaml))
                return ApiResults.BadRequest("yaml is required");

            var project = context.GetResolvedProject();
            try
            {
                var template = await manager.CreateTemplateAsync(project.Id, req.Yaml);
                return Results.Json(new { success = true, data = template }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        byRef.MapGet("/workflow-templates/{tid}", async (HttpContext context, string tid, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var def = await manager.GetTemplateAsync(project.Id, tid);
            return def is not null
                ? ApiResults.Ok(new { projectId = project.Id, templateId = tid, definition = def })
                : ApiResults.NotFound("Project template not found");
        });

        byRef.MapPut("/workflow-templates/{tid}", async (HttpContext context, string tid, UpdateProjectTemplateRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Yaml))
                return ApiResults.BadRequest("yaml is required");

            var project = context.GetResolvedProject();
            try
            {
                var template = await manager.UpdateTemplateAsync(project.Id, tid, req.Yaml);
                return template is not null
                    ? ApiResults.Ok(template)
                    : ApiResults.NotFound("Project template not found");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        byRef.MapDelete("/workflow-templates/{tid}", async (HttpContext context, string tid, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var deleted = await manager.DeleteTemplateAsync(project.Id, tid);
            return deleted ? ApiResults.Ok(new { deleted = true }) : ApiResults.NotFound("Project template not found");
        });

        // =======================================================================
        // Project workflow profile
        // =======================================================================

        byRef.MapGet("/workflow-profile", async (HttpContext context, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var templateId = await manager.GetDefaultTemplateAsync(project.Id);
            var variables = await manager.GetVariablesAsync(project.Id);
            var disabledIds = await manager.GetDisabledWorkflowProfileIdsAsync(project.Id);
            return ApiResults.Ok(new { projectId = project.Id, defaultTemplateId = templateId, variables, disabledWorkflowProfileIds = disabledIds });
        });

        byRef.MapPut("/workflow-profile/default-template", async (HttpContext context, SetDefaultTemplateRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.TemplateId))
                return ApiResults.BadRequest("templateId is required");

            var project = context.GetResolvedProject();
            var updated = await manager.SetDefaultTemplateAsync(project.Id, req.TemplateId);
            return updated is not null
                ? ApiResults.Ok(new { projectId = project.Id, defaultTemplateId = updated })
                : ApiResults.NotFound("Project workflow profile not found");
        });

        byRef.MapDelete("/workflow-profile/default-template", async (HttpContext context, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            await manager.SetDefaultTemplateAsync(project.Id, null);
            return ApiResults.Ok(new { projectId = project.Id, defaultTemplateId = (string?)null });
        });

        byRef.MapGet("/workflow-profile/variables", async (HttpContext context, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var variables = await manager.GetVariablesAsync(project.Id);
            return ApiResults.Ok(variables);
        });

        byRef.MapPut("/workflow-profile/variables", async (HttpContext context, VariableBundle bundle, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var result = await manager.SetVariablesAsync(project.Id, bundle);
            return ApiResults.Ok(result);
        });

        byRef.MapPatch("/workflow-profile/variables", async (HttpContext context, VariableBundle patch, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var result = await manager.PatchVariablesAsync(project.Id, patch);
            return ApiResults.Ok(result);
        });

        // =======================================================================
        // Workflow profile enable/disable
        // =======================================================================

        byRef.MapPost("/workflow-profile/disable", async (HttpContext context, ToggleWorkflowProfileRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.ProfileId))
                return ApiResults.BadRequest("profileId is required");

            var project = context.GetResolvedProject();
            try
            {
                await manager.SetProfileEnabledAsync(project.Id, req.ProfileId, enabled: false);
                return ApiResults.Ok();
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

        byRef.MapPost("/workflow-profile/enable", async (HttpContext context, ToggleWorkflowProfileRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.ProfileId))
                return ApiResults.BadRequest("profileId is required");

            var project = context.GetResolvedProject();
            try
            {
                await manager.SetProfileEnabledAsync(project.Id, req.ProfileId, enabled: true);
                return ApiResults.Ok();
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "unknown_workflow_profile");
            }
        });

        byRef.MapGet("/templates", async (HttpContext context, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var prompts = await manager.ListPromptsAsync(project.Id);
            return ApiResults.Ok(prompts.Select(ToTemplateRoutePrompt));
        });

        byRef.MapGet("/templates/{key}", async (HttpContext context, string key, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var prompt = await manager.GetPromptAsync(project.Id, key);
            return prompt is null
                ? ApiResults.NotFound($"Prompt '{key}' not found")
                : ApiResults.Ok(ToTemplateRoutePrompt(prompt));
        });

        byRef.MapGet("/templates/{key}/override", async (HttpContext context, string key, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var prompt = await manager.GetProjectPromptOverrideAsync(project.Id, key);
            return prompt is null
                ? ApiResults.NotFound($"Prompt override '{key}' not found")
                : ApiResults.Ok(prompt);
        });

        byRef.MapPut("/templates/{key}/override", async (HttpContext context, string key, ProjectPromptOverrideRequest? req, ProjectWorkflowProfileManager manager) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Body))
                return ApiResults.BadRequest("body is required");

            var project = context.GetResolvedProject();
            var prompt = await manager.SetProjectPromptOverrideAsync(project.Id, key, req.DisplayName, req.Description, req.Tags, req.Stage, req.Body);
            return ApiResults.Ok(prompt);
        });

        byRef.MapDelete("/templates/{key}/override", async (HttpContext context, string key, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            await manager.DeleteProjectPromptOverrideAsync(project.Id, key);
            return ApiResults.Ok();
        });

        byRef.MapPost("/templates/{key}/preview", async (HttpContext context, string key, PromptPreviewRequest? req, ProjectWorkflowProfileManager manager) =>
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
                var result = await manager.PreviewPromptAsync(project.Id, key, variables);
                return ApiResults.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.NotFound(ex.Message);
            }
        });

        byRef.MapGet("/workflow-profile/prompts", async (HttpContext context, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var prompts = await manager.ListPromptsAsync(project.Id);
            return ApiResults.Ok(prompts);
        });

        byRef.MapGet("/workflow-profile/prompts/{key}", async (HttpContext context, string key, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            var prompt = await manager.GetPromptAsync(project.Id, key);
            return prompt is null
                ? ApiResults.NotFound($"Prompt '{key}' not found")
                : ApiResults.Ok(prompt);
        });

        byRef.MapPut("/workflow-profile/prompts/{key}", async (HttpContext context, string key, PromptUpsertRequest? req, ProjectWorkflowProfileManager manager) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Body))
                return ApiResults.BadRequest("body is required");

            var project = context.GetResolvedProject();
            await manager.SetPromptAsync(project.Id, key, req.Body);
            return ApiResults.Ok(new { key, body = req.Body });
        });

        byRef.MapDelete("/workflow-profile/prompts/{key}", async (HttpContext context, string key, ProjectWorkflowProfileManager manager) =>
        {
            var project = context.GetResolvedProject();
            await manager.DeletePromptAsync(project.Id, key);
            return ApiResults.Ok();
        });

        byRef.MapPost("/workflow-profile/prompts/{key}/preview", async (HttpContext context, string key, PromptPreviewRequest? req, ProjectWorkflowProfileManager manager) =>
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
                var result = await manager.PreviewPromptAsync(project.Id, key, variables);
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

    private static bool TryGetRepositoryNameError(
        ArgumentException exception,
        string requestedName,
        out string? message)
    {
        message = exception.Message;
        return message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }
}

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
    JsonElement? IsDefault = null);
public record UpdateProjectRequest();
public record AddRepositoryRequest(
    string Name,
    string GitUrl,
    string? BaseBranch = null,
    bool? SetDefault = null,
    JsonElement? IsDefault = null);
public record UpdateRepositoryRequest(
    bool? SetDefault = null,
    string? GitUrl = null,
    string? BaseBranch = null,
    JsonElement? NewName = null,
    JsonElement? IsDefault = null);
public record CreateProjectTemplateRequest(string Yaml);
public record UpdateProjectTemplateRequest(string Yaml);
public record SetDefaultTemplateRequest(string TemplateId);

public sealed record ToggleWorkflowProfileRequest(string ProfileId);
