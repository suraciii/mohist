using Mohist.Server.Api;
using Mohist.Server.Runner.SignalR;
using Microsoft.AspNetCore.Http.Extensions;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistApiRegistration
{
    public static WebApplication MapMohistApi(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") &&
                !context.Request.Query.ContainsKey("projectId") &&
                context.Request.Headers.TryGetValue("X-Mohist-Project-Id", out var headerProjectId))
            {
                var projectId = headerProjectId.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(projectId))
                {
                    var query = new QueryBuilder();
                    foreach (var (key, values) in context.Request.Query)
                    {
                        foreach (var value in values)
                        {
                            query.Add(key, value ?? string.Empty);
                        }
                    }

                    query.Add("projectId", projectId);
                    context.Request.QueryString = query.ToQueryString();
                }
            }

            await next();
        });

        app.UseApiExceptionHandler();
        app.MapHealthRoutes();
        app.MapStatusRoutes();
        app.MapProjectRoutes();
        app.MapIssueRoutes();
        app.MapWorkflowProfileRoutes();
        app.MapWorkflowEventRoutes();
        app.MapWorkflowSessionRoutes();
        app.MapWorkflowTaskRoutes();
        app.MapEventRoutes();
        app.MapConfigRoutes();
        app.MapOpencodeRoutes();
        app.MapLabelsRoutes();
        app.MapLogsRoutes();
        app.MapFsRoutes();
        app.MapWorkspaceRoutes();
        app.MapEpicRoutes();
        app.MapAgentRoutes();
        app.MapRunnerRoutes();
        app.MapHub<RunnerHub>("/hubs/runner");
        return app;
    }
}
