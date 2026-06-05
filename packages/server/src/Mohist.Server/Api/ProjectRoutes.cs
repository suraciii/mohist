using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Domain;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using System.Text.Json;

namespace Mohist.Server.Api;

public static class ProjectRoutes
{
    public static WebApplication MapProjectRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("/", async (ProjectQuerier projectsQuery) =>
        {
            var projects = await projectsQuery.ListAllAsync();
            return ApiResults.Ok(projects);
        });

        group.MapGet("/{projectRef}", async (string projectRef, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/", async (CreateProjectRequest req, IGrainFactory grains, ProjectQuerier projectsQuery) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Path))
                return ApiResults.BadRequest("name and path are required");

            if (!ProjectName.TryNormalize(req.Name, out var projectName, out var nameError))
                return ApiResults.BadRequest(nameError!, "invalid_project_name");

            if (await projectsQuery.ExistsAsync(projectName))
                return ApiResults.Conflict($"Project '{projectName}' already exists");

            var id = $"proj_{Guid.NewGuid():N}";
            var projectGrain = grains.GetGrain<IProjectGrain>(id);
            try
            {
                var project = await projectGrain.CreateAsync(projectName, req.Path, req.BaseBranch);
                return Results.Json(new { success = true, data = project }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPost("/{projectRef}/use", async (string projectRef, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPatch("/{projectRef}", async (string projectRef, UpdateProjectRequest req, IGrainFactory grains, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            var updated = await projectGrain.UpdateAsync(req.BaseBranch);
            return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound("Project not found");
        });

        group.MapDelete("/{projectRef}", async (string projectRef, IGrainFactory grains, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null)
                return ApiResults.NotFound("Project not found");

            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            await projectGrain.DeleteAsync();
            return ApiResults.Ok();
        });

        group.MapGet("/{projectRef}/repositories", async (string projectRef, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            return project is not null ? ApiResults.Ok(project.Repositories) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/{projectRef}/repositories", async (string projectRef, AddRepositoryRequest req, IGrainFactory grains, ProjectRefResolver projects) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return ApiResults.BadRequest("name is required");
            if (string.IsNullOrWhiteSpace(req.Path) && string.IsNullOrWhiteSpace(req.Remote))
                return ApiResults.BadRequest("path or remote is required");

            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            try
            {
                var updated = await projectGrain.AddRepositoryAsync(req.Name, req.Path, req.Remote, req.BaseBranch);
                return updated is not null
                    ? Results.Json(new { success = true, data = updated }, statusCode: 201)
                    : ApiResults.NotFound("Project not found");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPatch("/{projectRef}/repositories/{repoName}", async (string projectRef, string repoName, UpdateRepositoryRequest req, IGrainFactory grains, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            if (req.SetDefault == true)
            {
                var updated = await projectGrain.SetDefaultRepositoryAsync(repoName);
                return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound("Project or repository not found");
            }
            return ApiResults.BadRequest("No action specified");
        });

        group.MapDelete("/{projectRef}/repositories/{repoName}", async (string projectRef, string repoName, IGrainFactory grains, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            var updated = await projectGrain.RemoveRepositoryAsync(repoName);
            return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound("Project or repository not found");
        });

        // =======================================================================
        // Project workflow templates CRUD
        // =======================================================================

        group.MapGet("/{projectRef}/workflow-templates", async (string projectRef, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var templates = await manager.ListTemplatesAsync(project.Id);
            return ApiResults.Ok(templates);
        });

        group.MapPost("/{projectRef}/workflow-templates", async (string projectRef, CreateProjectTemplateRequest req, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            if (string.IsNullOrWhiteSpace(req.Yaml))
                return ApiResults.BadRequest("yaml is required");

            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

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

        group.MapGet("/{projectRef}/workflow-templates/{tid}", async (string projectRef, string tid, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var def = await manager.GetTemplateAsync(project.Id, tid);
            return def is not null
                ? ApiResults.Ok(new { projectId = project.Id, templateId = tid, definition = def })
                : ApiResults.NotFound("Project template not found");
        });

        group.MapPut("/{projectRef}/workflow-templates/{tid}", async (string projectRef, string tid, UpdateProjectTemplateRequest req, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            if (string.IsNullOrWhiteSpace(req.Yaml))
                return ApiResults.BadRequest("yaml is required");

            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

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

        group.MapDelete("/{projectRef}/workflow-templates/{tid}", async (string projectRef, string tid, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var deleted = await manager.DeleteTemplateAsync(project.Id, tid);
            return deleted ? ApiResults.Ok(new { deleted = true }) : ApiResults.NotFound("Project template not found");
        });

        // =======================================================================
        // Project workflow profile
        // =======================================================================

        group.MapGet("/{projectRef}/workflow-profile", async (string projectRef, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var templateId = await manager.GetDefaultTemplateAsync(project.Id);
            var variables = await manager.GetVariablesAsync(project.Id);
            return ApiResults.Ok(new { projectId = project.Id, defaultTemplateId = templateId, variables });
        });

        group.MapPut("/{projectRef}/workflow-profile/default-template", async (string projectRef, SetDefaultTemplateRequest req, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            if (string.IsNullOrWhiteSpace(req.TemplateId))
                return ApiResults.BadRequest("templateId is required");

            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var updated = await manager.SetDefaultTemplateAsync(project.Id, req.TemplateId);
            return updated is not null
                ? ApiResults.Ok(new { projectId = project.Id, defaultTemplateId = updated })
                : ApiResults.NotFound("Project workflow profile not found");
        });

        group.MapDelete("/{projectRef}/workflow-profile/default-template", async (string projectRef, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            await manager.SetDefaultTemplateAsync(project.Id, null);
            return ApiResults.Ok(new { projectId = project.Id, defaultTemplateId = (string?)null });
        });

        group.MapGet("/{projectRef}/workflow-profile/variables", async (string projectRef, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var variables = await manager.GetVariablesAsync(project.Id);
            return ApiResults.Ok(variables);
        });

        group.MapPut("/{projectRef}/workflow-profile/variables", async (string projectRef, VariableBundle bundle, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var result = await manager.SetVariablesAsync(project.Id, bundle);
            return ApiResults.Ok(result);
        });

        group.MapPatch("/{projectRef}/workflow-profile/variables", async (string projectRef, VariableBundle patch, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var result = await manager.PatchVariablesAsync(project.Id, patch);
            return ApiResults.Ok(result);
        });

        group.MapGet("/{projectRef}/templates", async (string projectRef, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var prompts = await manager.ListPromptsAsync(project.Id);
            return ApiResults.Ok(prompts.Select(ToTemplateRoutePrompt));
        });

        group.MapGet("/{projectRef}/templates/{key}", async (string projectRef, string key, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var prompt = await manager.GetPromptAsync(project.Id, key);
            return prompt is null
                ? ApiResults.NotFound($"Prompt '{key}' not found")
                : ApiResults.Ok(ToTemplateRoutePrompt(prompt));
        });

        group.MapGet("/{projectRef}/templates/{key}/override", async (string projectRef, string key, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var prompt = await manager.GetProjectPromptOverrideAsync(project.Id, key);
            return prompt is null
                ? ApiResults.NotFound($"Prompt override '{key}' not found")
                : ApiResults.Ok(prompt);
        });

        group.MapPut("/{projectRef}/templates/{key}/override", async (string projectRef, string key, ProjectPromptOverrideRequest? req, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Body))
                return ApiResults.BadRequest("body is required");

            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var prompt = await manager.SetProjectPromptOverrideAsync(project.Id, key, req.DisplayName, req.Description, req.Tags, req.Stage, req.Body);

            return ApiResults.Ok(prompt);
        });

        group.MapDelete("/{projectRef}/templates/{key}/override", async (string projectRef, string key, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            await manager.DeleteProjectPromptOverrideAsync(project.Id, key);

            return ApiResults.Ok();
        });

        group.MapPost("/{projectRef}/templates/{key}/preview", async (string projectRef, string key, PromptPreviewRequest? req, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
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
                var project = await projects.ResolveAsync(projectRef);
                if (project is null) return ApiResults.NotFound("Project not found");

                var result = await manager.PreviewPromptAsync(project.Id, key, variables);
                return ApiResults.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.NotFound(ex.Message);
            }
        });

        group.MapGet("/{projectRef}/workflow-profile/prompts", async (string projectRef, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var prompts = await manager.ListPromptsAsync(project.Id);
            return ApiResults.Ok(prompts);
        });

        group.MapGet("/{projectRef}/workflow-profile/prompts/{key}", async (string projectRef, string key, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            var prompt = await manager.GetPromptAsync(project.Id, key);
            return prompt is null
                ? ApiResults.NotFound($"Prompt '{key}' not found")
                : ApiResults.Ok(prompt);
        });

        group.MapPut("/{projectRef}/workflow-profile/prompts/{key}", async (string projectRef, string key, PromptUpsertRequest? req, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Body))
                return ApiResults.BadRequest("body is required");

            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            await manager.SetPromptAsync(project.Id, key, req.Body);
            return ApiResults.Ok(new { key, body = req.Body });
        });

        group.MapDelete("/{projectRef}/workflow-profile/prompts/{key}", async (string projectRef, string key, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");

            await manager.DeletePromptAsync(project.Id, key);
            return ApiResults.Ok();
        });

        group.MapPost("/{projectRef}/workflow-profile/prompts/{key}/preview", async (string projectRef, string key, PromptPreviewRequest? req, ProjectWorkflowProfileManager manager, ProjectRefResolver projects) =>
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
                var project = await projects.ResolveAsync(projectRef);
                if (project is null) return ApiResults.NotFound("Project not found");

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
}

public sealed record PromptUpsertRequest(string? Body);

public sealed record ProjectPromptOverrideRequest(
    string? DisplayName,
    string? Description,
    string[]? Tags,
    string? Stage,
    string? Body);

public sealed record PromptPreviewRequest(JsonElement? Variables);

public record CreateProjectRequest(string Name, string Path, string? BaseBranch = null);
public record UpdateProjectRequest(string? BaseBranch = null);
public record AddRepositoryRequest(string Name, string? Path = null, string? Remote = null, string? BaseBranch = null);
public record UpdateRepositoryRequest(bool? SetDefault = null);
public record CreateProjectTemplateRequest(string Yaml);
public record UpdateProjectTemplateRequest(string Yaml);
public record SetDefaultTemplateRequest(string TemplateId);
