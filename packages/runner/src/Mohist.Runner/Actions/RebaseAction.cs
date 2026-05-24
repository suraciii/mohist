using System.Text.Json;

namespace Mohist.Runner.Actions;

public class RebaseAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var baseBranch = JsonInputs.String(context.With, "baseBranch") ?? "main";
        var before = await GitCommand.RunAsync(context.WorkDir, ["rev-parse", "HEAD"], context.CancellationToken);
        var result = await GitCommand.RunAsync(context.WorkDir, ["rebase", baseBranch], context.CancellationToken);
        var after = result.Success
            ? await GitCommand.RunAsync(context.WorkDir, ["rev-parse", "HEAD"], context.CancellationToken)
            : null;

        var output = JsonSerializer.Serialize(new
        {
            kind = "rebase",
            baseBranch,
            beforeHeadSha = before.Success ? before.Stdout.Trim() : null,
            afterHeadSha = after?.Success == true ? after.Stdout.Trim() : null,
            rebased = result.Success,
            output = result.CombinedOutput,
        });

        return result.Success
            ? new ActionResult("success", "Rebase completed", output, result.ExitCode)
            : new ActionResult("failure", result.CombinedOutput, output, result.ExitCode);
    }
}
