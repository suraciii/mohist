using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;

namespace Mohist.Server.Api;

public static class StatusRoutes
{
    private const string ProjectRegistryKey = "project-registry";

    public static WebApplication MapStatusRoutes(this WebApplication app)
    {
        app.MapGet("/api/status", async (bool? all, IGrainFactory grains) =>
        {
            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);

            if (all == true)
            {
                var projects = await registry.GetAllAsync();
                var currentId = (await registry.GetCurrentAsync())?.Id;
                var status = new List<object>();

                foreach (var project in projects)
                {
                    var catalog = grains.GetGrain<IIssueCatalogGrain>(project.Id);
                    var issues = await catalog.ListAsync(all: true);
                    var activeIssues = issues.Count(i => i.Status == "active");

                    status.Add(new
                    {
                        name = project.Name,
                        path = project.Path,
                        issues = issues.Count,
                        activeIssues,
                        isCurrent = currentId == project.Id
                    });
                }

                return ApiResults.Ok(status);
            }

            var current = await registry.GetCurrentAsync();
            if (current is null)
                return ApiResults.BadRequest("No active project. Use: mo project use <name>");

            var issueCatalog = grains.GetGrain<IIssueCatalogGrain>(current.Id);
            var allIssues = await issueCatalog.ListAsync(all: true);
            var active = allIssues.Where(i => i.Status == "active").ToList();

            var llm = new { configured = false };
            // TODO: read from config service when implemented

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
                llm,
                version = versionInfo.Version,
                gitHash = versionInfo.GitHash,
                sourceHead = versionInfo.SourceHead,
                upToDate = versionInfo.UpToDate,
            };

            return ApiResults.Ok(result);
        });

        return app;
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
