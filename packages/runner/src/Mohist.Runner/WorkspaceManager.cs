using System.Text.Json;
using Mohist.Runner.Actions;

namespace Mohist.Runner;

public class WorkspaceManager : IWorkspaceManager
{
    private readonly ILogger<WorkspaceManager> _log;
    private readonly string _runnerRoot;

    public WorkspaceManager(ILogger<WorkspaceManager> log, string? runnerRoot = null)
    {
        _log = log;
        _runnerRoot = runnerRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "projects");
    }

    public Task<WorkspaceInfo> EnsureAsync(Dictionary<string, JsonElement?> variables, CancellationToken ct)
    {
        var workspacePath = ResolveExistingWorkspace(variables);
        if (workspacePath is not null)
            return Task.FromResult(new WorkspaceInfo(workspacePath, null, ResolveChangeDir(variables, workspacePath)));

        workspacePath = ResolveProjectWorktree(variables, ct);
        var changeDir = ResolveChangeDir(variables, workspacePath);
        CreateDirectories(workspacePath, changeDir);

        return Task.FromResult(new WorkspaceInfo(workspacePath, ResolveBranch(variables), changeDir));
    }

    private static string? ResolveExistingWorkspace(Dictionary<string, JsonElement?> variables)
    {
        if (variables.TryGetValue("workspace", out var ws) &&
            ws is not null &&
            ws.Value.TryGetProperty("path", out var pathProp) &&
            pathProp.ValueKind == JsonValueKind.String)
        {
            var path = pathProp.GetString();
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path;
        }

        return null;
    }

    private string ResolveProjectWorktree(Dictionary<string, JsonElement?> variables, CancellationToken ct)
    {
        var projectId = ResolveString(variables, "project.id") ?? "default";
        var issueNumberStr = ResolveString(variables, "issue.number");
        var issueNumber = int.TryParse(issueNumberStr, out var n) ? n : 0;
        var issueDir = issueNumber > 0 ? $"issue-{issueNumber}" : "worktree";

        var path = Path.Combine(_runnerRoot, projectId, "worktrees", issueDir);

        _log.LogInformation("Resolved workspace path: {Path}", path);
        return path;
    }

    private static string? ResolveBranch(Dictionary<string, JsonElement?> variables)
    {
        var issueNumberStr = ResolveString(variables, "issue.number");
        var issueNumber = int.TryParse(issueNumberStr, out var n) ? n : 0;
        return issueNumber is > 0 ? $"mo/issue-{issueNumber}" : null;
    }

    private static string? ResolveChangeDir(Dictionary<string, JsonElement?> variables, string workspacePath)
    {
        var changeDir = ResolveString(variables, "artifacts.changeDir");
        if (string.IsNullOrWhiteSpace(changeDir)) return null;

        var fullPath = Path.Combine(workspacePath, changeDir!);
        return fullPath;
    }

    private static void CreateDirectories(string workspace, string? changeDir)
    {
        Directory.CreateDirectory(workspace);

        if (changeDir is not null)
        {
            Directory.CreateDirectory(changeDir);
            Directory.CreateDirectory(Path.Combine(changeDir, "specs"));
        }
    }

    private static string? ResolveString(Dictionary<string, JsonElement?> variables, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !variables.TryGetValue(parts[0], out var current) || current is null)
            return null;

        var element = current.Value;
        for (var i = 1; i < parts.Length; i++)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(parts[i], out element))
                return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }
}
