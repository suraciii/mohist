using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

public static class StatusRoutes
{
    public static WebApplication MapStatusRoutes(this WebApplication app)
    {
        app.MapGet("/api/status", async (bool? all, string? projectId, IGrainFactory grains, IssueQuerier issuesQuery, ProjectQuerier projectsQuery) =>
        {
            if (all == true)
            {
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
            }

            var current = await ResolveProjectAsync(projectId, projectsQuery);
            if (current is null)
                return ApiResults.BadRequest("No active project. Pass projectId or create/select a project in the web UI.");

            var allIssues = await issuesQuery.ListAsync(current.Id, current, all: true);
            var active = allIssues.Where(i => i.Health == "active").ToList();

            var versionInfo = GetVersionInfo();

            var result = new
            {
                name = current.Name,
                path = current.Path,
                issues = allIssues.Count,
                activeIssues = active.Count,
                issuesByStatus = new Dictionary<string, int>
                {
                    ["backlog"] = allIssues.Count(i => i.Status == "backlog"),
                    ["ready"] = allIssues.Count(i => i.Status == "ready" || i.Status == "todo"),
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

    private static async Task<ProjectInfo?> ResolveProjectAsync(string? projectId, ProjectQuerier projectsQuery)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
            return await projectsQuery.GetByIdAsync(projectId);

        return await projectsQuery.ResolveSingleAsync();
    }

    private static (string? Version, string? GitHash, string? SourceHead, bool UpToDate) GetVersionInfo()
    {
        var version = typeof(StatusRoutes).Assembly.GetName().Version?.ToString();
        var gitHash = Environment.GetEnvironmentVariable("MOHIST_GIT_HASH");
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
