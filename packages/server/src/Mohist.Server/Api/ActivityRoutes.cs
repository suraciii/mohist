using Microsoft.AspNetCore.Http;
using Mohist.Server.AgentOps.Services;

namespace Mohist.Server.Api;

public static class ActivityRoutes
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 200;

    public static WebApplication MapActivityRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/activity")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("", async (HttpContext context, int? limit, ActivityEvidenceAssembler activity, CancellationToken ct) =>
        {
            var effectiveLimit = limit ?? DefaultLimit;
            if (effectiveLimit is < 1 or > MaxLimit)
                return ApiResults.BadRequest($"limit must be between 1 and {MaxLimit}", "invalid_limit", new { limit = effectiveLimit });

            var project = context.GetResolvedProject();
            return ApiResults.Ok(await activity.ListAsync(project.Id, effectiveLimit, ct));
        });

        return app;
    }
}
