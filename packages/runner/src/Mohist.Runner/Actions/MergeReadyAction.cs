using System.Text.Json;

namespace Mohist.Runner.Actions;

public class MergeReadyAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var baseBranch = JsonInputs.String(context.With, "baseBranch")
            ?? ResolveVariable(context.Variables, "project.defaultBranch")
            ?? "main";

        var baseResult = await GitCommand.RunAsync(context.WorkDir, ["rev-parse", baseBranch], context.CancellationToken);
        if (!baseResult.Success)
            return Result(false, baseBranch, null, null, null, $"Could not resolve base branch '{baseBranch}'", baseResult.ExitCode);

        var headResult = await GitCommand.RunAsync(context.WorkDir, ["rev-parse", "HEAD"], context.CancellationToken);
        if (!headResult.Success)
            return Result(false, baseBranch, baseResult.Stdout.Trim(), null, null, "Could not resolve HEAD", headResult.ExitCode);

        var mergeBaseResult = await GitCommand.RunAsync(context.WorkDir, ["merge-base", baseBranch, "HEAD"], context.CancellationToken);
        var mergeBase = mergeBaseResult.Success ? mergeBaseResult.Stdout.Trim() : null;

        var mergeTreeResult = await GitCommand.RunAsync(context.WorkDir, ["merge-tree", "--write-tree", baseBranch, "HEAD"], context.CancellationToken);
        if (!mergeTreeResult.Success)
            return Result(false, baseBranch, baseResult.Stdout.Trim(), headResult.Stdout.Trim(), mergeBase, mergeTreeResult.CombinedOutput, mergeTreeResult.ExitCode);

        return Result(true, baseBranch, baseResult.Stdout.Trim(), headResult.Stdout.Trim(), mergeBase, null, mergeTreeResult.ExitCode);
    }

    private static ActionResult Result(bool canMerge, string baseBranch, string? baseSha, string? headSha, string? mergeBaseSha, string? error, int? exitCode)
    {
        var output = JsonSerializer.Serialize(new
        {
            kind = "merge-ready",
            targetBranch = baseBranch,
            baseSha = baseSha ?? "",
            candidateHeadSha = headSha ?? "",
            mergeBaseSha = mergeBaseSha ?? "",
            canMerge,
            conflictFiles = Array.Empty<string>(),
            checkedAt = DateTime.UtcNow.ToString("o"),
            error,
        });

        return canMerge
            ? new ActionResult("success", "Merge ready", output, exitCode)
            : new ActionResult("failure", error ?? "Merge is not ready", output, exitCode);
    }

    private static string? ResolveVariable(Dictionary<string, JsonElement?>? variables, string path)
    {
        if (variables is null) return null;
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !variables.TryGetValue(parts[0], out var current) || current is null)
            return null;

        var element = current.Value;
        for (var i = 1; i < parts.Length; i++)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(parts[i], out element))
                return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }
}
