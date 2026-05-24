using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workspace;

namespace Mohist.Server.Api;

public static class WorkspaceRoutes
{
    private const string ProjectRegistryKey = "project-registry";

    public static WebApplication MapWorkspaceRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/issues/{number:int}");

        issues.MapGet("/worktree-status", async (int number, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var status = await git.GetWorktreeStatusAsync(project.Path, project.Name, number, project.BaseBranch);
            return ApiResults.Ok(status);
        });

        issues.MapGet("/diff", async (int number, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var branchExists = await git.BranchExistsAsync(project.Path, $"mo/issue-{number}");
            if (!branchExists)
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            var head = $"mo/issue-{number}";
            var diff = await git.GetDiffAsync(project.Path, project.BaseBranch, head);
            var mergeBase = await git.GetMergeBaseAsync(project.Path, project.BaseBranch, head) ?? project.BaseBranch;
            var (ahead, behind) = await git.GetAheadBehindAsync(project.Path, project.BaseBranch, head);
            var commits = await git.GetCommitsAsync(project.Path, project.BaseBranch, head);
            return ApiResults.Ok(new
            {
                available = true,
                reason = (string?)null,
                @base = project.BaseBranch,
                head,
                mergeBase,
                ahead,
                behind,
                canFastForward = behind == 0,
                comparison = "merge-base",
                summary = new { filesChanged = diff.Files.Count, commits = commits.Length, additions = diff.TotalAdditions, deletions = diff.TotalDeletions },
                files = diff.Files,
            });
        });

        issues.MapGet("/commits", async (int number, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var head = $"mo/issue-{number}";
            if (!await git.BranchExistsAsync(project.Path, head))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            var commits = await git.GetCommitsAsync(project.Path, project.BaseBranch, head);
            var diff = await git.GetDiffAsync(project.Path, project.BaseBranch, head);
            var mergeBase = await git.GetMergeBaseAsync(project.Path, project.BaseBranch, head) ?? project.BaseBranch;
            var (ahead, behind) = await git.GetAheadBehindAsync(project.Path, project.BaseBranch, head);
            return ApiResults.Ok(new
            {
                available = true,
                reason = (string?)null,
                @base = project.BaseBranch,
                head,
                mergeBase,
                ahead,
                behind,
                canFastForward = behind == 0,
                comparison = "merge-base",
                summary = new { filesChanged = diff.Files.Count, commits = commits.Length, additions = diff.TotalAdditions, deletions = diff.TotalDeletions },
                commits = commits.Select(c => new
                {
                    c.Hash,
                    c.ShortHash,
                    c.Message,
                    c.Author,
                    c.Date,
                    filesChanged = c.Files.Length,
                    additions = 0,
                    deletions = 0,
                    c.Files,
                }),
            });
        });

        issues.MapGet("/commits/{hash}/diff", async (int number, string hash, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var head = $"mo/issue-{number}";
            if (!await git.BranchExistsAsync(project.Path, head))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found", hash, diff = "" });

            var diff = await git.GetCommitDiffAsync(project.Path, hash);
            return diff is null
                ? ApiResults.NotFound($"Commit {hash} not found")
                : ApiResults.Ok(new { available = true, reason = (string?)null, hash, diff });
        });

        issues.MapGet("/file-content", async (int number, string path, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var baseContent = await git.GetFileContentAsync(project.Path, project.BaseBranch, path);
            var headContent = await git.GetFileContentAsync(project.Path, $"mo/issue-{number}", path);

            return ApiResults.Ok(new { @base = baseContent, head = headContent });
        });

        issues.MapPost("/cleanup", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                var info = await grain.GetInfoAsync();
                // TODO: actually remove worktree
                return ApiResults.Ok(new { message = $"Issue #{number} worktree cleanup queued" });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        return app;
    }

    private static async Task<string?> ResolveProjectIdAsync(IGrainFactory grains)
    {
        var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
        var current = await registry.GetCurrentAsync();
        return current?.Id;
    }
}
