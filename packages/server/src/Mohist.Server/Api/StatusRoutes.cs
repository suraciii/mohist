using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Grains;

namespace Mohist.Server.Api;

public static class StatusRoutes
{
    private const string ProjectKey = "projects";

    public static WebApplication MapStatusRoutes(this WebApplication app)
    {
        app.MapGet("/api/status", async (bool? all, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);

            if (all == true)
            {
                var projects = await projectsGrain.GetAllAsync();
                var status = new List<object>();

                foreach (var project in projects)
                {
                    var issues = await issuesQuery.ListAsync(project.Id, project, all: true);
                    var activeIssues = issues.Count(i => i.RuntimeStatus == "active");

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

            var current = await ResolveProjectAsync(projectId, projectsGrain);
            if (current is null)
                return ApiResults.BadRequest("No active project. Pass projectId or create/select a project in the web UI.");

            var allIssues = await issuesQuery.ListAsync(current.Id, current, all: true);
            var active = allIssues.Where(i => i.RuntimeStatus == "active").ToList();

            var versionInfo = GetVersionInfo();

            var result = new
            {
                name = current.Name,
                path = current.Path,
                issues = allIssues.Count,
                activeIssues = active.Count,
                issuesByStage = new Dictionary<string, int>
                {
                    ["backlog"] = allIssues.Count(i => i.Stage == "backlog"),
                    ["plan"] = allIssues.Count(i => i.Stage == "plan"),
                    ["build"] = allIssues.Count(i => i.Stage == "build"),
                    ["check"] = allIssues.Count(i => i.Stage == "check"),
                    ["done"] = allIssues.Count(i => i.Stage == "done"),
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

    private static async Task<Mohist.Server.Project.Queries.ProjectInfo?> ResolveProjectAsync(string? projectId, IProjectGrain projectsGrain)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
            return await projectsGrain.GetByIdAsync(projectId);

        var projects = await projectsGrain.GetAllAsync();
        return projects.Count == 1 ? projects[0] : null;
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
