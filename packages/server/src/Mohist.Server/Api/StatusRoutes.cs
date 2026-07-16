using Microsoft.AspNetCore.Http;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SystemInfo;

namespace Mohist.Server.Api;

public static class StatusRoutes
{
    public static WebApplication MapStatusRoutes(this WebApplication app)
    {
        app.MapGet("/api/status", async (bool? all, IssueQuerier issuesQuery, ProjectQuerier projectsQuery) =>
        {
            if (all != true)
                return ApiResults.BadRequest("Use /api/projects/{projectRef}/status for project status.");

            var projects = await projectsQuery.ListAllAsync();
            var status = new List<object>();

            foreach (var project in projects)
            {
                var issues = await issuesQuery.ListAsync(project.Id, project, all: true);
                var activeIssues = issues.Count(i => i.Health == "active");

                status.Add(new
                {
                    name = project.Name,
                    issues = issues.Count,
                    activeIssues
                });
            }

            return ApiResults.Ok(status);
        });

        var byRef = app.MapGroup("/api/projects/{projectRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        byRef.MapGet("/status", async (HttpContext context, IssueQuerier issuesQuery, IRuntimeBuildInfo runtimeBuildInfo, IRuntimeSourceIdentity sourceIdentity) =>
        {
            var current = context.GetResolvedProject();

            var allIssues = await issuesQuery.ListAsync(current.Id, current, all: true);
            var active = allIssues.Where(i => i.Health == "active").ToList();

            var sourceHead = sourceIdentity.GitHead;

            var result = new
            {
                name = current.Name,
                issues = allIssues.Count,
                activeIssues = active.Count,
                issuesByStatus = new Dictionary<string, int>
                {
                    ["backlog"] = allIssues.Count(i => i.Status == "backlog"),
                    ["in_progress"] = allIssues.Count(i => i.Status == "in_progress"),
                    ["done"] = allIssues.Count(i => i.Status == "done"),
                    ["cancelled"] = allIssues.Count(i => i.Status == "cancelled"),
                },
                version = runtimeBuildInfo.Version,
                gitHash = runtimeBuildInfo.GitHash,
                sourceHead,
                upToDate = runtimeBuildInfo.GitHash != null
                    && sourceHead != null
                    && runtimeBuildInfo.GitHash == sourceHead,
            };

            return ApiResults.Ok(result);
        });

        return app;
    }

}
