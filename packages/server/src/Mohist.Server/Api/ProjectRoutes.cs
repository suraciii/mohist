using Mohist.Server.Project.Grains;

namespace Mohist.Server.Api;

public static class ProjectRoutes
{
    private const string ProjectKey = "projects";

    public static WebApplication MapProjectRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("/", async (IGrainFactory grains) =>
        {
            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var projects = await projectsGrain.GetAllAsync();
            return ApiResults.Ok(projects);
        });

        group.MapGet("/current", () =>
        {
            return ApiResults.NotFound("Current project is selected by the client");
        });

        group.MapGet("/{name}", async (string name, IGrainFactory grains) =>
        {
            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByNameAsync(name);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/", async (CreateProjectRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Path))
                return ApiResults.BadRequest("name and path are required");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            try
            {
                var project = await projectsGrain.CreateAsync(req.Name, req.Path, req.BaseBranch);
                return Results.Json(new { success = true, data = project }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPatch("/{name}", async (string name, UpdateProjectRequest req, IGrainFactory grains) =>
        {
            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.UpdateAsync(name, req.BaseBranch);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/{name}/use", async (string name, IGrainFactory grains) =>
        {
            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByNameAsync(name);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapDelete("/{name}", async (string name, IGrainFactory grains) =>
        {
            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var deleted = await projectsGrain.DeleteAsync(name);
            return deleted ? ApiResults.Ok() : ApiResults.NotFound("Project not found");
        });

        return app;
    }
}

public record CreateProjectRequest(string Name, string Path, string? BaseBranch = null);
public record UpdateProjectRequest(string? BaseBranch = null);
