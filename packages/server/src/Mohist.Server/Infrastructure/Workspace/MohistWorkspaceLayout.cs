using Mohist.Server.SystemInfo;

namespace Mohist.Server.Infrastructure.Workspace;

public static class MohistWorkspaceLayout
{
    public const string RunnerRootEnvironmentVariable = "MOHIST_RUNNER_ROOT";
    public const string WorkspaceRootEnvironmentVariable = "MOHIST_WORKSPACE_ROOT";

    public static string DefaultRunnerRoot()
    {
        return DefaultRunnerRoot(SystemEnvironmentVariableProvider.Instance);
    }

    public static string DefaultRunnerRoot(IEnvironmentVariableProvider environment)
    {
        var configured = environment.GetEnvironmentVariable(RunnerRootEnvironmentVariable)
            ?? environment.GetEnvironmentVariable(WorkspaceRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "projects");
    }

    public static string IssueWorktreePath(string runnerRoot, string projectName, int issueNumber)
        => Path.GetFullPath(Path.Combine(runnerRoot, Slug(projectName), "worktrees", $"issue-{issueNumber}"));

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }
}
