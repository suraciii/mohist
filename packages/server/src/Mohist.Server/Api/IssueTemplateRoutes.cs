using Mohist.Server.Issue.Domain.IssueTemplate;
using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Issue template list/get endpoints.
/// Deliberately separate from <see cref="TemplateRoutes"/> (prompt templates)
/// and not nested under /api/workflow-*.
/// </summary>
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

            var template = registry.Get(name, project.Id!);
            var source = template.Id == IssueTemplates.DefaultId ? "builtin" : "custom";

            var detail = new IssueTemplateDetail(
                template.Id,
                template.Name,
                template.About,
                template.IsDefault,
                template.SuitableFor,
                template.Defaults,
                template.Sections,
                source);

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
    string About,
    bool IsDefault,
    IReadOnlyList<string> SuitableFor,
    IssueTemplateDefaults Defaults,
    IReadOnlyList<IssueTemplateSection> Sections,
    string Source);
