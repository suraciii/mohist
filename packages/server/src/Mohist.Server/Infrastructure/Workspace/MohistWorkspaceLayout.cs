namespace Mohist.Server.Infrastructure.Workspace;

public static class MohistWorkspaceLayout
{
    public static string DefaultRunnerRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MOHIST_RUNNER_ROOT")
            ?? Environment.GetEnvironmentVariable("MOHIST_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "projects");
    }

    public static string IssueWorktreePath(string runnerRoot, string projectName, int issueNumber)
        => Path.GetFullPath(Path.Combine(runnerRoot, Slug(projectName), "worktrees", $"issue-{issueNumber}"));

    public static string LegacyIssueWorktreePath(string projectPath, string projectName, int issueNumber)
        => Path.GetFullPath(Path.Combine(projectPath, "..", ".mohist-worktrees", projectName, issueNumber.ToString()));

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }
}
