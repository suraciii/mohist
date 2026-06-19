using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;
using Mohist.Server.Label.Services;
using System.Text.Json;

namespace Mohist.Server.Api;

public static class LabelsRoutes
{
    public static WebApplication MapLabelsRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/labels")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (
            HttpContext ctx,
            string projectRef,
            IssueQuerier issuesQuery) =>
        {
            var project = IssueRoutes.GetRequiredProject(ctx);
            var issues = await issuesQuery.ListAsync(project.Id, project, all: true);
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var issue in issues)
            {
                if (issue.Labels is null) continue;
                foreach (var key in issue.Labels.Keys)
                    keys.Add(key);
            }
            return ApiResults.Ok(keys.ToArray());
        });

        group.MapGet("/catalog", async (HttpContext ctx, LabelCatalogService catalog) =>
        {
            var project = ctx.GetResolvedProject();
            var definitions = await catalog.ListAsync(project.Id);
            return ApiResults.Ok(definitions);
        });

        group.MapPost("/catalog", async (HttpContext ctx, CreateLabelDefinitionRequest req, LabelCatalogService catalog) =>
        {
            var project = ctx.GetResolvedProject();
            var result = await catalog.CreateAsync(project.Id, req.Key, req.Description, req.SupportedValues);
            if (result.Error is not null)
            {
                if (result.Error.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("non-empty", StringComparison.OrdinalIgnoreCase))
                    return ApiResults.BadRequest(result.Error);
                return ApiResults.Conflict(result.Error);
            }
            return Results.Json(new ApiResponse<LabelDefinition>(true, result.Definition), statusCode: 201);
        });

        group.MapPatch("/catalog/{key}", async (HttpContext ctx, string key, JsonElement req, LabelCatalogService catalog) =>
        {
            var project = ctx.GetResolvedProject();

            string? description = null;
            IReadOnlyList<string>? supportedValues = null;
            var hasDescription = false;
            var hasSupportedValues = false;
            JsonElement descriptionElement = default;
            JsonElement supportedValuesElement = default;
            if (req.ValueKind == JsonValueKind.Object)
            {
                hasDescription = req.TryGetProperty("description", out descriptionElement);
                hasSupportedValues = req.TryGetProperty("supportedValues", out supportedValuesElement);
            }
            if (hasDescription && descriptionElement.ValueKind != JsonValueKind.Null)
                description = descriptionElement.GetString();
            if (hasSupportedValues && supportedValuesElement.ValueKind != JsonValueKind.Null)
                supportedValues = supportedValuesElement.Deserialize<IReadOnlyList<string>>();

            if (!hasDescription || !hasSupportedValues)
            {
                var definitions = await catalog.ListAsync(project.Id);
                var current = definitions.FirstOrDefault(d => d.Key == key);
                if (current is null)
                    return ApiResults.NotFound($"Key '{key}' not found in the project catalog.");
                if (!hasDescription)
                    description = current.Description;
                if (!hasSupportedValues)
                    supportedValues = current.SupportedValues;
            }

            var result = await catalog.UpdateAsync(project.Id, key, description!, supportedValues);
            if (result.Error is not null)
            {
                if (result.NotFound)
                    return ApiResults.NotFound(result.Error);
                if (result.Error.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("non-empty", StringComparison.OrdinalIgnoreCase))
                    return ApiResults.BadRequest(result.Error);
                return ApiResults.Conflict(result.Error);
            }
            return ApiResults.Ok(result.Definition);
        });

        group.MapDelete("/catalog/{key}", async (HttpContext ctx, string key, LabelCatalogService catalog) =>
        {
            var project = ctx.GetResolvedProject();
            var result = await catalog.DeleteAsync(project.Id, key);
            if (result.Error is not null)
                return ApiResults.Conflict(result.Error);
            return Results.NoContent();
        });

        return app;
    }
}

public sealed record CreateLabelDefinitionRequest(
    string Key,
    string Description,
    IReadOnlyList<string>? SupportedValues = null);

public sealed record UpdateLabelDefinitionRequest(
    string? Description = null,
    IReadOnlyList<string>? SupportedValues = null);
