using Mohist.Server.Workflow.Prompts;
using Mohist.Server.Workflow.Prompts.Domain;
using Mohist.Server.Workflow.Prompts.Infrastructure;

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

        app.MapPost("/api/templates/extract-variables", (ExtractVariablesRequest? request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Body))
                return ApiResults.BadRequest("body is required");

            var variables = PromptTemplateEngine.ExtractVariables(request.Body);
            return ApiResults.Ok(new ExtractVariablesResponse(variables));
        });

        return app;
    }
}

public record ExtractVariablesRequest(string? Body);
public record ExtractVariablesResponse(IReadOnlyList<string> Variables);
