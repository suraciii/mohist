using System.Text.Json;

namespace Mohist.Runner.Actions;

public class RebaseAction : IAction
{
    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var baseBranch = JsonInputs.String(context.With, "baseBranch") ?? "main";
        var conflictResolver = JsonInputs.Element(context.With, "conflictResolver");
        var before = await GitCommand.RunAsync(context.WorkDir, ["rev-parse", "HEAD"], context.CancellationToken);
        var result = await GitCommand.RunAsync(context.WorkDir, ["rebase", baseBranch], context.CancellationToken);
        var after = result.Success
            ? await GitCommand.RunAsync(context.WorkDir, ["rev-parse", "HEAD"], context.CancellationToken)
            : null;
        var conflicts = result.Success
            ? []
            : await GetConflictFilesAsync(context.WorkDir, context.CancellationToken);

        if (!result.Success && conflicts.Count > 0 && conflictResolver is { ValueKind: JsonValueKind.Object })
        {
            var requestedTask = BuildRequestedTask(conflictResolver.Value, conflicts, baseBranch);
            var conflictOutput = JsonSerializer.Serialize(new
            {
                kind = "rebase",
                status = "conflict",
                baseBranch,
                beforeHeadSha = before.Success ? before.Stdout.Trim() : null,
                conflicts,
                output = result.CombinedOutput,
                requestedTask,
            });

            return new ActionResult("failure", "Rebase conflict; conflict resolver task requested", conflictOutput, result.ExitCode);
        }

        var output = JsonSerializer.Serialize(new
        {
            kind = "rebase",
            status = result.Success ? "completed" : conflicts.Count > 0 ? "conflict" : "failed",
            baseBranch,
            beforeHeadSha = before.Success ? before.Stdout.Trim() : null,
            afterHeadSha = after?.Success == true ? after.Stdout.Trim() : null,
            rebased = result.Success,
            conflicts,
            output = result.CombinedOutput,
        });

        return result.Success
            ? new ActionResult("success", "Rebase completed", output, result.ExitCode)
            : new ActionResult("failure", result.CombinedOutput, output, result.ExitCode);
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

    private static object BuildRequestedTask(JsonElement conflictResolver, List<string> conflicts, string baseBranch)
    {
        var id = conflictResolver.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : "resolve-rebase-conflicts";
        var uses = conflictResolver.TryGetProperty("uses", out var usesProp) && usesProp.ValueKind == JsonValueKind.String
            ? usesProp.GetString()
            : "mohist/coder-agent";
        var title = conflictResolver.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
            ? titleProp.GetString()
            : "Resolve rebase conflicts";

        var with = conflictResolver.TryGetProperty("with", out var withProp) && withProp.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(withProp.GetRawText()) ?? []
            : [];

        if (!with.ContainsKey("stage"))
            with["stage"] = JsonSerializer.SerializeToElement("maintenance");
        if (!with.ContainsKey("task"))
            with["task"] = JsonSerializer.SerializeToElement("resolve-rebase-conflicts");
        if (!with.ContainsKey("conflicts"))
            with["conflicts"] = JsonSerializer.SerializeToElement(conflicts);
        if (!with.ContainsKey("description"))
            with["description"] = JsonSerializer.SerializeToElement("Resolve git rebase conflicts, stage resolved files, and continue the rebase until it completes.");

        return new
        {
            id,
            title,
            uses,
            with,
            then = new
            {
                id = "verify-rebase",
                title = "Verify rebase completed",
                uses = "mohist/rebase-status",
                with = new Dictionary<string, object?>
                {
                    ["baseBranch"] = baseBranch
                }
            },
        };
    }
}
