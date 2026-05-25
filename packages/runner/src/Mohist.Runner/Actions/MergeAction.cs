using System.Text.Json;

namespace Mohist.Runner.Actions;

public class MergeAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var source = JsonInputs.String(context.With, "source") ?? "HEAD";
        var target = JsonInputs.String(context.With, "target");
        var strategy = JsonInputs.String(context.With, "strategy") ?? "squash";
        var message = JsonInputs.String(context.With, "message") ?? "Mohist merge";
        var mergeWorkDir = ResolveMergeWorkDir(context);

        var sourceCommit = await CommitPendingSourceChangesAsync(context.WorkDir, message, context.CancellationToken);
        if (!sourceCommit.Success)
            return Failure(source, target, strategy, sourceCommit);

        if (!string.IsNullOrWhiteSpace(target))
        {
            var checkout = await GitCommand.RunAsync(mergeWorkDir, ["checkout", target], context.CancellationToken);
            if (!checkout.Success)
                return Failure(source, target, strategy, checkout);
        }

        var result = strategy.Equals("squash", StringComparison.OrdinalIgnoreCase)
            ? await SquashMergeAsync(mergeWorkDir, context.CancellationToken, source, message)
            : await GitCommand.RunAsync(mergeWorkDir, ["merge", source], context.CancellationToken);

        var head = result.Success
            ? await GitCommand.RunAsync(mergeWorkDir, ["rev-parse", "HEAD"], context.CancellationToken)
            : null;

        var output = JsonSerializer.Serialize(new
        {
            kind = "merge",
            source,
            target,
            strategy,
            workDir = mergeWorkDir,
            sourceCommitted = sourceCommit.CombinedOutput,
            landedSha = head?.Success == true ? head.Stdout.Trim() : null,
            output = result.CombinedOutput,
        });

        return result.Success
            ? new ActionResult("success", "Merge completed", output, result.ExitCode)
            : new ActionResult("failure", result.CombinedOutput, output, result.ExitCode);
    }

    private static ActionResult Failure(string source, string? target, string strategy, GitCommandResult result)
    {
        var output = JsonSerializer.Serialize(new
        {
            kind = "merge",
            source,
            target,
            strategy,
            landedSha = (string?)null,
            output = result.CombinedOutput,
        });
        return new ActionResult("failure", result.CombinedOutput, output, result.ExitCode);
    }

    private static async Task<GitCommandResult> SquashMergeAsync(string workDir, CancellationToken ct, string source, string message)
    {
        var merge = await GitCommand.RunAsync(workDir, ["merge", "--squash", source], ct);
        if (!merge.Success) return merge;

        return await GitCommand.RunAsync(workDir, ["commit", "-m", message], ct);
    }

    private static async Task<GitCommandResult> CommitPendingSourceChangesAsync(string workDir, string message, CancellationToken ct)
    {
        var status = await GitCommand.RunAsync(workDir, ["status", "--porcelain"], ct);
        if (!status.Success) return status;
        if (string.IsNullOrWhiteSpace(status.Stdout)) return new GitCommandResult(0, "", "");

        var add = await GitCommand.RunAsync(workDir, ["add", "."], ct);
        if (!add.Success) return add;

        return await GitCommand.RunAsync(workDir, ["commit", "-m", $"{message} integration"], ct);
    }

    private static string ResolveMergeWorkDir(ActionContext context)
    {
        var projectPath = new VariableBag(context.Variables).String("project.path");
        return string.IsNullOrWhiteSpace(projectPath)
            ? context.WorkDir
            : Path.GetFullPath(projectPath);
    }
}
