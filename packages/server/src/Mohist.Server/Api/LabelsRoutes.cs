using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;

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
        return app;
    }
}
