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
            var project = await registry.GetByNameAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var status = await git.GetWorktreeStatusAsync(project.Path, project.Name, number, project.BaseBranch);
            return ApiResults.Ok(status);
        });

        issues.MapGet("/diff", async (int number, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByNameAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var branchExists = await git.BranchExistsAsync(project.Path, $"mo/issue-{number}");
            if (!branchExists)
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            var diff = await git.GetDiffAsync(project.Path, project.BaseBranch, $"mo/issue-{number}");
            return ApiResults.Ok(new
            {
                available = true,
                reason = (string?)null,
                base_ = project.BaseBranch,
                head = $"mo/issue-{number}",
                summary = new { filesChanged = diff.Files.Count, additions = diff.TotalAdditions, deletions = diff.TotalDeletions },
                files = diff.Files,
            });
        });

        issues.MapGet("/file-content", async (int number, string path, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByNameAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var baseContent = await git.GetFileContentAsync(project.Path, project.BaseBranch, path);
            var headContent = await git.GetFileContentAsync(project.Path, $"mo/issue-{number}", path);

            return ApiResults.Ok(new { base_ = baseContent, head = headContent });
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
