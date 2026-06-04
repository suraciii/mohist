using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Prompts;
using Mohist.Server.Workflow.Prompts.Domain;
using Mohist.Server.Workflow.Prompts.Infrastructure;

namespace Mohist.Server.Api;

public static class ProjectTemplateRoutes
{
    public static WebApplication MapProjectTemplateRoutes(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectId}/templates", async (
            string projectId,
            IPromptLoader promptLoader,
            IProjectTemplateStore templateStore) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return ApiResults.BadRequest("projectId is required");

            var systemTemplates = promptLoader.LoadAllTemplates();
            var projectOverrides = await templateStore.GetForProjectAsync(projectId);
            var overrideByKey = projectOverrides.ToDictionary(o => o.Key, StringComparer.Ordinal);

            var keys = new SortedSet<string>(systemTemplates.Keys, StringComparer.Ordinal);
            foreach (var ov in projectOverrides)
                keys.Add(ov.Key);

            var entries = new List<EffectiveTemplateResponse>(keys.Count);
            foreach (var key in keys)
            {
                if (overrideByKey.TryGetValue(key, out var ov))
                {
                    var source = systemTemplates.ContainsKey(key) ? "project-override" : "project-new";
                    entries.Add(EffectiveTemplateResponse.FromOverride(ov, source));
                }
                else
                {
                    var system = systemTemplates[key];
                    entries.Add(EffectiveTemplateResponse.FromSystem(key, system));
                }
            }

            return ApiResults.Ok(entries);
        });

        app.MapGet("/api/projects/{projectId}/templates/{key}", async (
            string projectId,
            string key,
            IPromptLoader promptLoader,
            IProjectTemplateStore templateStore) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return ApiResults.BadRequest("projectId is required");
            if (string.IsNullOrWhiteSpace(key))
                return ApiResults.BadRequest("key is required");

            var ov = await templateStore.GetAsync(projectId, key);
            if (ov is not null)
            {
                var systemTemplates = promptLoader.LoadAllTemplates();
                var source = systemTemplates.ContainsKey(key) ? "project-override" : "project-new";
                return ApiResults.Ok(EffectiveTemplateResponse.FromOverride(ov, source));
            }

            var systemTemplatesOnly = promptLoader.LoadAllTemplates();
            if (systemTemplatesOnly.TryGetValue(key, out var system))
                return ApiResults.Ok(EffectiveTemplateResponse.FromSystem(key, system));

            return ApiResults.NotFound($"Template '{key}' not found");
        });

        app.MapGet("/api/projects/{projectId}/templates/{key}/override", async (
            string projectId,
            string key,
            IProjectTemplateStore templateStore) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return ApiResults.BadRequest("projectId is required");
            if (string.IsNullOrWhiteSpace(key))
                return ApiResults.BadRequest("key is required");

            var ov = await templateStore.GetAsync(projectId, key);
            return ov is null
                ? ApiResults.NotFound($"Override for '{key}' not found")
                : ApiResults.Ok(ProjectOverrideResponse.FromDomain(ov));
        });

        app.MapPut("/api/projects/{projectId}/templates/{key}/override", async (
            string projectId,
            string key,
            ProjectTemplateOverrideRequest? request,
            IProjectTemplateStore templateStore,
            IEventStore events) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return ApiResults.BadRequest("projectId is required");
            if (string.IsNullOrWhiteSpace(key))
                return ApiResults.BadRequest("key is required");
            if (request is null || string.IsNullOrWhiteSpace(request.Body))
                return ApiResults.BadRequest("body is required");

            var tags = request.Tags ?? Array.Empty<string>();
            var before = await templateStore.GetAsync(projectId, key);
            var stored = await templateStore.UpsertAsync(
                projectId,
                key,
                request.Body,
                request.DisplayName ?? string.Empty,
                request.Description ?? string.Empty,
                tags,
                request.Stage);

            await events.AppendAsync(new EventInput(
                projectId,
                IssueNumber: 0,
                Category: "project",
                Type: "project_template_changed",
                Message: key,
                Payload: new
                {
                    key,
                    before,
                    after = stored,
                    source = "user",
                }));

            return ApiResults.Ok(ProjectOverrideResponse.FromDomain(stored));
        });

        app.MapDelete("/api/projects/{projectId}/templates/{key}/override", async (
            string projectId,
            string key,
            IProjectTemplateStore templateStore,
            IEventStore events) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return ApiResults.BadRequest("projectId is required");
            if (string.IsNullOrWhiteSpace(key))
                return ApiResults.BadRequest("key is required");

            var before = await templateStore.GetAsync(projectId, key);
            await templateStore.DeleteAsync(projectId, key);

            if (before is not null)
            {
                await events.AppendAsync(new EventInput(
                    projectId,
                    IssueNumber: 0,
                    Category: "project",
                    Type: "project_template_deleted",
                    Message: key,
                    Payload: new
                    {
                        key,
                        before,
                        after = (ProjectTemplate?)null,
                        source = "user",
                    }));
            }

            return ApiResults.Ok();
        });

        app.MapPost("/api/projects/{projectId}/templates/{key}/preview", async (
            string projectId,
            string key,
            PreviewTemplateRequest? request,
            IPromptLoader promptLoader,
            IProjectTemplateStore templateStore,
            PromptTemplateEngine engine) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return ApiResults.BadRequest("projectId is required");
            if (string.IsNullOrWhiteSpace(key))
                return ApiResults.BadRequest("key is required");

            string body;
            var ov = await templateStore.GetAsync(projectId, key);
            if (ov is not null)
            {
                body = ov.Body;
            }
            else
            {
                var systemTemplates = promptLoader.LoadAllTemplates();
                if (!systemTemplates.TryGetValue(key, out var system))
                    return ApiResults.NotFound($"Template '{key}' not found");
                body = system.Body;
            }

            JsonElement variables;
            if (request?.Variables is { } raw)
            {
                variables = raw;
            }
            else
            {
                using var doc = JsonDocument.Parse("{}");
                variables = doc.RootElement.Clone();
            }
            var (rendered, missing, depth) = engine.Render(body, variables);
            return ApiResults.Ok(new PreviewTemplateResponse(rendered, missing, depth));
        });

        return app;
    }
}

public sealed record EffectiveTemplateResponse(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    string? Stage,
    string Body,
    string Source)
{
    public static EffectiveTemplateResponse FromSystem(string key, SystemTemplate template) =>
        new(key, template.DisplayName, template.Description, template.Tags, template.Stage, template.Body, "system");

    public static EffectiveTemplateResponse FromOverride(ProjectTemplate template, string source) =>
        new(template.Key, template.DisplayName, template.Description, template.Tags, template.Stage, template.Body, source);
}

public sealed record ProjectOverrideResponse(
    string ProjectId,
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Tags,
    string? Stage,
    string Body,
    DateTime UpdatedAt)
{
    public static ProjectOverrideResponse FromDomain(ProjectTemplate template) =>
        new(template.ProjectId, template.Key, template.DisplayName, template.Description, template.Tags, template.Stage, template.Body, template.UpdatedAt);
}

public sealed record ProjectTemplateOverrideRequest(
    string? DisplayName,
    string? Description,
    IReadOnlyList<string>? Tags,
    string? Stage,
    string? Body);

public sealed record PreviewTemplateRequest(JsonElement? Variables);

public sealed record PreviewTemplateResponse(
    string Rendered,
    IReadOnlyList<string> MissingVariables,
    int Depth);
