using Microsoft.Extensions.Configuration;
using Mohist.Server.SystemInfo;
using System.Security.Cryptography;
using System.Text;

namespace Mohist.Server.Infrastructure.Workspace;

public static class MohistWorkspaceLayout
{
    public const string RunnerRootEnvironmentVariable = "MOHIST_RUNNER_ROOT";
    public const string WorkspaceRootEnvironmentVariable = "MOHIST_WORKSPACE_ROOT";
    public const string RunnerRootConfigurationKey = "Mohist:RunnerRoot";

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

    public static string ResolveRunnerRoot(IConfiguration configuration, IEnvironmentVariableProvider environment)
    {
        var configured = configuration[RunnerRootConfigurationKey]
            ?? environment.GetEnvironmentVariable(RunnerRootEnvironmentVariable)
            ?? environment.GetEnvironmentVariable(WorkspaceRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "projects");
    }

    public static string IssueWorkspacePath(string runnerRoot, string projectName, int issueNumber)
        => Path.GetFullPath(Path.Combine(runnerRoot, Slug(projectName), "workspaces", $"issue-{issueNumber}"));

    public static string WorkflowRunWorkspacePath(string runnerRoot, string workflowRunId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workflowRunId))).ToLowerInvariant();
        return Path.GetFullPath(Path.Combine(runnerRoot, "workspaces", $"run-{hash}"));
    }

    /// <summary>
    /// Slugifies a project name to a safe directory component. MUST stay in
    /// sync with the runner's <c>slug()</c> helper in
    /// <c>packages/runner/src/runtime/workspace.ts</c> so the server-computed
    /// workspace path and the runner-computed cache path are identical.
    /// </summary>
    public static string Slug(string value)
    {
        if (string.IsNullOrEmpty(value)) return "project";
        var lowered = value.ToLowerInvariant();
        var buffer = new System.Text.StringBuilder(lowered.Length);
        var previousWasSeparator = true;
        foreach (var raw in lowered)
        {
            var isAllowed = (raw >= 'a' && raw <= 'z') || (raw >= '0' && raw <= '9');
            if (isAllowed)
            {
                buffer.Append(raw);
                previousWasSeparator = false;
            }
            else
            {
                if (!previousWasSeparator)
                {
                    buffer.Append('-');
                    previousWasSeparator = true;
                }
            }
        }
        // Trim trailing separator introduced by the run
        while (buffer.Length > 0 && buffer[buffer.Length - 1] == '-')
        {
            buffer.Length -= 1;
        }
        return buffer.Length == 0 ? "project" : buffer.ToString();
    }
}
