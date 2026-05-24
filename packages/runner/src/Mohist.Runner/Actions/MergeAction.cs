using System.Text.Json;

namespace Mohist.Runner.Actions;

public class MergeAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var source = JsonInputs.String(context.With, "source") ?? "HEAD";
        var strategy = JsonInputs.String(context.With, "strategy") ?? "squash";
        var message = JsonInputs.String(context.With, "message") ?? "Mohist merge";

        var result = strategy.Equals("squash", StringComparison.OrdinalIgnoreCase)
            ? await SquashMergeAsync(context, source, message)
            : await GitCommand.RunAsync(context.WorkDir, ["merge", source], context.CancellationToken);

        var head = result.Success
            ? await GitCommand.RunAsync(context.WorkDir, ["rev-parse", "HEAD"], context.CancellationToken)
            : null;

        var output = JsonSerializer.Serialize(new
        {
            kind = "merge",
            source,
            strategy,
            landedSha = head?.Success == true ? head.Stdout.Trim() : null,
            output = result.CombinedOutput,
        });

        return result.Success
            ? new ActionResult("success", "Merge completed", output, result.ExitCode)
            : new ActionResult("failure", result.CombinedOutput, output, result.ExitCode);
    }

    private static async Task<GitCommandResult> SquashMergeAsync(ActionContext context, string source, string message)
    {
        var merge = await GitCommand.RunAsync(context.WorkDir, ["merge", "--squash", source], context.CancellationToken);
        if (!merge.Success) return merge;

        return await GitCommand.RunAsync(context.WorkDir, ["commit", "-m", message], context.CancellationToken);
    }
}
