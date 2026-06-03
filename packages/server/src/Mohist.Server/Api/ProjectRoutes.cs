using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Queries;
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

        group.MapGet("/{id}/variables", async (string id, IGrainFactory grains) =>
        {
            var variables = await grains.GetGrain<IProjectGrain>(id).GetVariablesAsync();
            return variables is not null ? ApiResults.Ok(variables) : ApiResults.NotFound("Project not found");
        });

        group.MapPatch("/{id}/variables/vars/{name}", async (string id, string name, JsonElement value, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return ApiResults.BadRequest("name is required");

            var variables = await grains.GetGrain<IProjectGrain>(id).PatchVariableAsync(name, value);
            return variables is not null ? ApiResults.Ok(variables) : ApiResults.NotFound("Project not found");
        });

        group.MapDelete("/{id}/variables/vars/{name}", async (string id, string name, IGrainFactory grains) =>
        {
            var variables = await grains.GetGrain<IProjectGrain>(id).DeleteVariableAsync(name);
            return variables is not null ? ApiResults.Ok(variables) : ApiResults.NotFound("Project not found");
        });

        group.MapPatch("/{id}/variables/stages/{stage}/vars/{name}", async (string id, string stage, string name, JsonElement value, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(name))
                return ApiResults.BadRequest("stage and name are required");

            var variables = await grains.GetGrain<IProjectGrain>(id).PatchStageVariableAsync(stage, name, value);
            return variables is not null ? ApiResults.Ok(variables) : ApiResults.NotFound("Project not found");
        });

        group.MapDelete("/{id}/variables/stages/{stage}/vars/{name}", async (string id, string stage, string name, IGrainFactory grains) =>
        {
            var variables = await grains.GetGrain<IProjectGrain>(id).DeleteStageVariableAsync(stage, name);
            return variables is not null ? ApiResults.Ok(variables) : ApiResults.NotFound("Project not found");
        });

        return app;
    }
}

public record CreateProjectRequest(string Name, string Path, string? BaseBranch = null);
public record UpdateProjectRequest(string? BaseBranch = null);
public record AddRepositoryRequest(string Name, string? Path = null, string? Remote = null, string? BaseBranch = null);
public record UpdateRepositoryRequest(bool? SetDefault = null);
