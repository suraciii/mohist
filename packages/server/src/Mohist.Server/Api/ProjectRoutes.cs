using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Queries;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Infrastructure;
using System.Text.Json;

namespace Mohist.Server.Api;

public static class ProjectRoutes
{
    public static WebApplication MapProjectRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("/", async (ProjectQueryService projectsQuery) =>
        {
            var projects = await projectsQuery.ListAllAsync();
            return ApiResults.Ok(projects);
        });

        group.MapGet("/{identifier}", async (string identifier, ProjectQueryService projectsQuery) =>
        {
            var project = await projectsQuery.ResolveByIdOrNameAsync(identifier);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/", async (CreateProjectRequest req, IGrainFactory grains, ProjectQueryService projectsQuery) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Path))
                return ApiResults.BadRequest("name and path are required");

            if (await projectsQuery.ExistsAsync(req.Name))
                return ApiResults.Conflict($"Project '{req.Name}' already exists");

            var id = $"proj_{Guid.NewGuid():N}";
            var projectGrain = grains.GetGrain<IProjectGrain>(id);
            try
            {
                var project = await projectGrain.CreateAsync(req.Name, req.Path, req.BaseBranch);
                return Results.Json(new { success = true, data = project }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPost("/{identifier}/use", async (string identifier, ProjectQueryService projectsQuery) =>
        {
            var project = await projectsQuery.ResolveByIdOrNameAsync(identifier);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPatch("/{id}", async (string id, UpdateProjectRequest req, IGrainFactory grains) =>
        {
            var projectGrain = grains.GetGrain<IProjectGrain>(id);
            var updated = await projectGrain.UpdateAsync(req.BaseBranch);
            return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound("Project not found");
        });

        group.MapDelete("/{identifier}", async (string identifier, IGrainFactory grains, ProjectQueryService projectsQuery) =>
        {
            var project = await projectsQuery.ResolveByIdOrNameAsync(identifier);
            if (project is null)
                return ApiResults.NotFound("Project not found");

            var projectGrain = grains.GetGrain<IProjectGrain>(project.Id);
            await projectGrain.DeleteAsync();
            return ApiResults.Ok();
        });

        group.MapGet("/{id}/repositories", async (string id, ProjectQueryService projectsQuery) =>
        {
            var project = await projectsQuery.GetByIdAsync(id);
            return project is not null ? ApiResults.Ok(project.Repositories) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/{id}/repositories", async (string id, AddRepositoryRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return ApiResults.BadRequest("name is required");
            if (string.IsNullOrWhiteSpace(req.Path) && string.IsNullOrWhiteSpace(req.Remote))
                return ApiResults.BadRequest("path or remote is required");

            var projectGrain = grains.GetGrain<IProjectGrain>(id);
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

        group.MapPatch("/{id}/repositories/{repoName}", async (string id, string repoName, UpdateRepositoryRequest req, IGrainFactory grains) =>
        {
            var projectGrain = grains.GetGrain<IProjectGrain>(id);
            if (req.SetDefault == true)
            {
                var updated = await projectGrain.SetDefaultRepositoryAsync(repoName);
                return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound("Project or repository not found");
            }
            return ApiResults.BadRequest("No action specified");
        });

        group.MapDelete("/{id}/repositories/{repoName}", async (string id, string repoName, IGrainFactory grains) =>
        {
            var projectGrain = grains.GetGrain<IProjectGrain>(id);
            var updated = await projectGrain.RemoveRepositoryAsync(repoName);
            return updated is not null ? ApiResults.Ok(updated) : ApiResults.NotFound("Project or repository not found");
        });

        // =======================================================================
        // Project Templates CRUD
        // =======================================================================

        group.MapGet("/{id}/templates", async (string id, ProjectWorkflowProfileManager manager) =>
        {
            var templates = await manager.ListTemplatesAsync(id);
            return ApiResults.Ok(templates);
        });

        group.MapPost("/{id}/templates", async (string id, CreateProjectTemplateRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Yaml))
                return ApiResults.BadRequest("yaml is required");

            try
            {
                var template = await manager.CreateTemplateAsync(id, req.Yaml);
                return Results.Json(new { success = true, data = template }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        group.MapGet("/{id}/templates/{tid}", async (string id, string tid, ProjectWorkflowProfileManager manager) =>
        {
            var def = await manager.GetTemplateAsync(id, tid);
            return def is not null
                ? ApiResults.Ok(new { projectId = id, templateId = tid, definition = def })
                : ApiResults.NotFound("Project template not found");
        });

        group.MapPut("/{id}/templates/{tid}", async (string id, string tid, UpdateProjectTemplateRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Yaml))
                return ApiResults.BadRequest("yaml is required");

            try
            {
                var template = await manager.UpdateTemplateAsync(id, tid, req.Yaml);
                return template is not null
                    ? ApiResults.Ok(template)
                    : ApiResults.NotFound("Project template not found");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        group.MapDelete("/{id}/templates/{tid}", async (string id, string tid, ProjectWorkflowProfileManager manager) =>
        {
            var deleted = await manager.DeleteTemplateAsync(id, tid);
            return deleted ? ApiResults.Ok(new { deleted = true }) : ApiResults.NotFound("Project template not found");
        });

        // =======================================================================
        // Project Default Template
        // =======================================================================

        group.MapGet("/{id}/default-template", async (string id, ProjectWorkflowProfileManager manager) =>
        {
            var templateId = await manager.GetDefaultTemplateAsync(id);
            return ApiResults.Ok(new { projectId = id, defaultTemplateId = templateId });
        });

        group.MapPut("/{id}/default-template", async (string id, SetDefaultTemplateRequest req, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(req.TemplateId))
                return ApiResults.BadRequest("templateId is required");

            var updated = await manager.SetDefaultTemplateAsync(id, req.TemplateId);
            return updated is not null
                ? ApiResults.Ok(new { projectId = id, defaultTemplateId = updated })
                : ApiResults.NotFound("Project workflow profile not found. Create one first via PUT/PATCH /api/projects/{id}/variables");
        });

        // =======================================================================
        // Project Variables
        // =======================================================================

        group.MapGet("/{id}/variables", async (string id, ProjectWorkflowProfileManager manager) =>
        {
            var variables = await manager.GetVariablesAsync(id);
            return ApiResults.Ok(variables);
        });

        group.MapPut("/{id}/variables", async (string id, VariableBundle bundle, ProjectWorkflowProfileManager manager) =>
        {
            var result = await manager.SetVariablesAsync(id, bundle);
            return ApiResults.Ok(result);
        });

        group.MapPatch("/{id}/variables", async (string id, VariableBundle patch, ProjectWorkflowProfileManager manager) =>
        {
            var result = await manager.PatchVariablesAsync(id, patch);
            return ApiResults.Ok(result);
        });

        group.MapPatch("/{id}/variables/vars/{name}", async (string id, string name, JsonElement value, IGrainFactory grains, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return ApiResults.BadRequest("name is required");

            await grains.GetGrain<IProjectGrain>(id).PatchVariableAsync(name, value);
            var result = await manager.PatchVariablesAsync(id, new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new Dictionary<string, JsonElement?> { [name] = value }))));
            return ApiResults.Ok(result);
        });

        group.MapDelete("/{id}/variables/vars/{name}", async (string id, string name, IGrainFactory grains, ProjectWorkflowProfileManager manager) =>
        {
            var result = await grains.GetGrain<IProjectGrain>(id).DeleteVariableAsync(name);
            return result is not null
                ? ApiResults.Ok(await manager.GetVariablesAsync(id))
                : ApiResults.NotFound("Project not found");
        });

        group.MapPatch("/{id}/variables/stages/{stage}/vars/{name}", async (string id, string stage, string name, JsonElement value, IGrainFactory grains, ProjectWorkflowProfileManager manager) =>
        {
            if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(name))
                return ApiResults.BadRequest("stage and name are required");

            await grains.GetGrain<IProjectGrain>(id).PatchStageVariableAsync(stage, name, value);
            var result = await manager.PatchVariablesAsync(id, new VariableBundle(
                Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
                {
                    [stage] = new StageVariables(JsonSerializer.Deserialize<JsonElement>(
                        JsonSerializer.Serialize(new Dictionary<string, JsonElement?> { [name] = value })))
                }));
            return ApiResults.Ok(result);
        });

        group.MapDelete("/{id}/variables/stages/{stage}/vars/{name}", async (string id, string stage, string name, IGrainFactory grains, ProjectWorkflowProfileManager manager) =>
        {
            var result = await grains.GetGrain<IProjectGrain>(id).DeleteStageVariableAsync(stage, name);
            return result is not null
                ? ApiResults.Ok(await manager.GetVariablesAsync(id))
                : ApiResults.NotFound("Project not found");
        });

        return app;
    }
}

public record CreateProjectRequest(string Name, string Path, string? BaseBranch = null);
public record UpdateProjectRequest(string? BaseBranch = null);
public record AddRepositoryRequest(string Name, string? Path = null, string? Remote = null, string? BaseBranch = null);
public record UpdateRepositoryRequest(bool? SetDefault = null);
public record CreateProjectTemplateRequest(string Yaml);
public record UpdateProjectTemplateRequest(string Yaml);
public record SetDefaultTemplateRequest(string TemplateId);
