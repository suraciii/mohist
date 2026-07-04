using Mohist.Server.Issue.Domain.IssueTemplate;
using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

public static class IssueTemplateRoutes
{
    public static WebApplication MapIssueTemplateRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/issue-templates");

        group.MapGet("/", async (IssueTemplateRegistry registry, ProjectRefResolver resolver, HttpContext ctx) =>
        {
            var project = await ResolveProjectAsync(ctx, resolver);
            if (project.Result is not null) return project.Result;
            var list = registry.List(project.Id!);
            return ApiResults.Ok(list);
        });

        group.MapGet("/{*name}", async (string name, IssueTemplateRegistry registry, ProjectRefResolver resolver, HttpContext ctx) =>
        {
            var project = await ResolveProjectAsync(ctx, resolver);
            if (project.Result is not null) return project.Result;

            if (!registry.Exists(name, project.Id!))
                return ApiResults.NotFound($"Issue template '{name}' not found");

            var lookup = registry.GetWithSource(name, project.Id!);
            var template = lookup.Template;

            var detail = new IssueTemplateDetail(
                template.Id,
                template.Name,
                template.Description,
                template.Body,
                lookup.Source);

            return ApiResults.Ok(detail);
        });

        return app;
    }

    private static async Task<ProjectResolution> ResolveProjectAsync(HttpContext ctx, ProjectRefResolver resolver)
    {
        var projectRef = ctx.Request.Query["projectId"].FirstOrDefault()
            ?? ctx.Request.Headers["X-Mohist-Project"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(projectRef))
            return new ProjectResolution(null, ApiResults.BadRequest("No active project"));

        var project = await resolver.ResolveAsync(projectRef);
        return project is null
            ? new ProjectResolution(null, ApiResults.NotFound("Project not found"))
            : new ProjectResolution(project.Id, null);
    }

    private sealed record ProjectResolution(string? Id, IResult? Result);
}

public sealed record IssueTemplateDetail(
    string Id,
    string Name,
    string Description,
    string Body,
    string Source);
