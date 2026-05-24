using System.Text.Json;

namespace Mohist.Runner.Actions;

public class AiReviewAction : IAction
{
    private readonly IAgentExecutor _executor;

    public AiReviewAction(IAgentExecutor executor)
    {
        _executor = executor;
    }

    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var changeDir = ResolveChangeDir(context);
        if (string.IsNullOrWhiteSpace(changeDir))
            return new ActionResult("failure", "AI review requires 'changeDir'");

        Directory.CreateDirectory(changeDir);
        var reviewPath = Path.Combine(changeDir, "review.md");
        var request = new AgentExecutionRequest(
            "check",
            "ai-review",
            changeDir,
            context.WorkDir,
            BuildPrompt(changeDir, reviewPath),
            context.CancellationToken);

        var result = await _executor.ExecuteAsync(request);
        var output = JsonSerializer.Serialize(new
        {
            kind = "ai-review",
            changeDir,
            reviewPath,
            result.Stdout,
            result.Stderr,
        });

        return result.ExitCode == 0
            ? new ActionResult("success", "AI review completed", output, result.ExitCode)
            : new ActionResult("failure", result.Stderr ?? result.Stdout ?? $"AI review exited with code {result.ExitCode}", output, result.ExitCode);
    }

    private static string? ResolveChangeDir(ActionContext context)
    {
        var changeDir = JsonInputs.String(context.With, "changeDir");
        if (string.IsNullOrWhiteSpace(changeDir)) return null;
        return Path.IsPathRooted(changeDir) ? changeDir : Path.GetFullPath(Path.Combine(context.WorkDir, changeDir));
    }

    private static string BuildPrompt(string changeDir, string reviewPath) => $$"""
    You are running Mohist Check stage AI review.

    Review the current workspace implementation and the change artifacts in:
    {{changeDir}}

    Write the review result to:
    {{reviewPath}}

    The review must identify blocking correctness, test, integration, or product contract issues.
    End the file with exactly one marker:
    <promise>PASS</promise>
    or
    <promise>FAIL</promise>
    """;
}
