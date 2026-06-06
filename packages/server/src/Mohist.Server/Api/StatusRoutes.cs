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
                    path = project.Path,
                    issues = issues.Count,
                    activeIssues
                });
            }

            return ApiResults.Ok(status);
        });

        app.MapGet("/api/projects/{projectRef}/status", async (string projectRef, IssueQuerier issuesQuery, ProjectRefResolver projects, IEnvironmentVariableProvider environment) =>
        {
            var current = await projects.ResolveAsync(projectRef);
            if (current is null) return ApiResults.NotFound("Project not found");

            var allIssues = await issuesQuery.ListAsync(current.Id, current, all: true);
            var active = allIssues.Where(i => i.Health == "active").ToList();

            var versionInfo = GetVersionInfo(environment);

            var result = new
            {
                name = current.Name,
                path = current.Path,
                issues = allIssues.Count,
                activeIssues = active.Count,
                issuesByStatus = new Dictionary<string, int>
                {
                    ["backlog"] = allIssues.Count(i => i.Status == "backlog"),
                    ["in_progress"] = allIssues.Count(i => i.Status == "in_progress"),
                    ["done"] = allIssues.Count(i => i.Status == "done"),
                    ["cancelled"] = allIssues.Count(i => i.Status == "cancelled"),
                },
                version = versionInfo.Version,
                gitHash = versionInfo.GitHash,
                sourceHead = versionInfo.SourceHead,
                upToDate = versionInfo.UpToDate,
            };

            return ApiResults.Ok(result);
        });

        return app;
    }

    private static (string? Version, string? GitHash, string? SourceHead, bool UpToDate) GetVersionInfo(IEnvironmentVariableProvider environment)
    {
        var version = typeof(StatusRoutes).Assembly.GetName().Version?.ToString();
        var gitHash = environment.GetEnvironmentVariable(RuntimeBuildInfo.GitHashEnvironmentVariable);
        var sourceHead = GetGitHead();
        var upToDate = gitHash != null && sourceHead != null && gitHash == sourceHead;
        return (version, gitHash, sourceHead, upToDate);
    }

    private static string? GetGitHead()
    {
        try
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !Directory.Exists(Path.Combine(root, ".git")))
            {
                root = Directory.GetParent(root)?.FullName;
            }
            if (root == null) return null;

            var headFile = Path.Combine(root, ".git", "HEAD");
            if (!File.Exists(headFile)) return null;

            var head = File.ReadAllText(headFile).Trim();
            if (head.StartsWith("ref: "))
            {
                var refPath = head[5..];
                var refFile = Path.Combine(root, ".git", refPath);
                if (File.Exists(refFile))
                    return File.ReadAllText(refFile).Trim();
            }
            else
            {
                return head;
            }
        }
        catch { }
        return null;
    }
}
