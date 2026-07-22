using System.Text.Json;
using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Api;

public static class TemplateRoutes
{
    public static WebApplication MapTemplateRoutes(this WebApplication app)
    {
        app.MapGet("/api/templates/system", (IPromptLoader promptLoader) =>
        {
            var templates = promptLoader.LoadAllTemplates();
            var sorted = templates
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value)
                .ToArray();
            return ApiResults.Ok(sorted);
        });

        app.MapPost("/api/templates/extract-variables", (ExtractVariablesRequest? request, PromptTemplateEngine engine) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Body))
                return ApiResults.BadRequest("body is required");

            var variables = PromptTemplateEngine.ExtractVariables(request.Body);
            var errors = request.Variables is { } context
                ? engine.Render(request.Body, context).Errors
                : Array.Empty<TemplateRenderError>();
            return ApiResults.Ok(new ExtractVariablesResponse(variables, errors));
        });

        return app;
    }
}

public record ExtractVariablesRequest(string? Body, JsonElement? Variables = null);
public record ExtractVariablesResponse(
    IReadOnlyList<string> Variables,
    IReadOnlyList<TemplateRenderError> Errors);
