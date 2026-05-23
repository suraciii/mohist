using Mohist.Server.Project.Grains;

namespace Mohist.Server.Api;

public static class ProjectRoutes
{
    private const string RegistryKey = "project-registry";

    public static WebApplication MapProjectRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("/", async (IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IProjectRegistryGrain>(RegistryKey);
            var projects = await registry.GetAllAsync();
            return ApiResults.Ok(projects);
        });

        group.MapGet("/current", async (IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IProjectRegistryGrain>(RegistryKey);
            var project = await registry.GetCurrentAsync();
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("No current project");
        });

        group.MapGet("/{name}", async (string name, IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IProjectRegistryGrain>(RegistryKey);
            var project = await registry.GetByNameAsync(name);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/", async (CreateProjectRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Path))
                return ApiResults.BadRequest("name and path are required");

            var registry = grains.GetGrain<IProjectRegistryGrain>(RegistryKey);
            try
            {
                var project = await registry.CreateAsync(req.Name, req.Path, req.BaseBranch);
                return Results.Json(new { success = true, data = project }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        group.MapPatch("/{name}", async (string name, UpdateProjectRequest req, IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IProjectRegistryGrain>(RegistryKey);
            var project = await registry.UpdateAsync(name, req.BaseBranch);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapPost("/{name}/use", async (string name, IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IProjectRegistryGrain>(RegistryKey);
            var project = await registry.SetCurrentAsync(name);
            return project is not null ? ApiResults.Ok(project) : ApiResults.NotFound("Project not found");
        });

        group.MapDelete("/{name}", async (string name, IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IProjectRegistryGrain>(RegistryKey);
            var deleted = await registry.DeleteAsync(name);
            return deleted ? ApiResults.Ok() : ApiResults.NotFound("Project not found");
        });

        return app;
    }
}

public record CreateProjectRequest(string Name, string Path, string? BaseBranch = null);
public record UpdateProjectRequest(string? BaseBranch = null);
