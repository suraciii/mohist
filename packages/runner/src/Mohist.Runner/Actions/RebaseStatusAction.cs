using System.Text.Json;

namespace Mohist.Runner.Actions;

public class RebaseStatusAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var baseBranch = JsonInputs.String(context.With, "baseBranch") ?? "main";
        var conflicts = await GetConflictFilesAsync(context.WorkDir, context.CancellationToken);
        var rebaseInProgress = await IsRebaseInProgressAsync(context.WorkDir, context.CancellationToken);
        var head = await GitCommand.RunAsync(context.WorkDir, ["rev-parse", "HEAD"], context.CancellationToken);
        var baseResult = await GitCommand.RunAsync(context.WorkDir, ["rev-parse", baseBranch], context.CancellationToken);
        var mergeBase = baseResult.Success
            ? await GitCommand.RunAsync(context.WorkDir, ["merge-base", baseBranch, "HEAD"], context.CancellationToken)
            : null;
        var verified = !rebaseInProgress
            && conflicts.Count == 0
            && head.Success
            && baseResult.Success
            && mergeBase?.Success == true
            && mergeBase.Stdout.Trim() == baseResult.Stdout.Trim();

        var output = JsonSerializer.Serialize(new
        {
            kind = "rebase-status",
            status = verified ? "verified" : "failed",
            baseBranch,
            rebaseInProgress,
            conflicts,
            baseSha = baseResult.Success ? baseResult.Stdout.Trim() : null,
            headSha = head.Success ? head.Stdout.Trim() : null,
            mergeBaseSha = mergeBase?.Success == true ? mergeBase.Stdout.Trim() : null,
            output = string.Join("\n", new[]
            {
                baseResult.CombinedOutput,
                mergeBase?.CombinedOutput,
            }.Where(s => !string.IsNullOrWhiteSpace(s))),
        });

        return verified
            ? new ActionResult("success", "Rebase verified", output)
            : new ActionResult("failure", "Rebase is not complete or not clean", output);
    }

    private static async Task<List<string>> GetConflictFilesAsync(string workDir, CancellationToken ct)
    {
        var status = await GitCommand.RunAsync(workDir, ["diff", "--name-only", "--diff-filter=U"], ct);
        if (!status.Success || string.IsNullOrWhiteSpace(status.Stdout)) return [];
        return status.Stdout
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToList();
    }

    private static async Task<bool> IsRebaseInProgressAsync(string workDir, CancellationToken ct)
    {
        var gitDir = await GitCommand.RunAsync(workDir, ["rev-parse", "--git-path", "rebase-merge"], ct);
        if (gitDir.Success && Directory.Exists(ResolveGitPath(workDir, gitDir.Stdout.Trim()))) return true;

        gitDir = await GitCommand.RunAsync(workDir, ["rev-parse", "--git-path", "rebase-apply"], ct);
        return gitDir.Success && Directory.Exists(ResolveGitPath(workDir, gitDir.Stdout.Trim()));
    }

    private static string ResolveGitPath(string workDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
}
