using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mohist.Runner.Actions;

public class AgentAction : IAction
{
    private readonly IAgentExecutor _executor;

    public AgentAction(IAgentExecutor executor)
    {
        _executor = executor;
    }

    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var stage = JsonInputs.String(context.With, "stage") ?? context.Stage;
        var task = JsonInputs.String(context.With, "task") ?? context.WorkId;
        var changeDir = ResolveChangeDir(context);

        if (string.IsNullOrWhiteSpace(stage))
            return new ActionResult("failure", "Agent action requires 'stage'");
        if (string.IsNullOrWhiteSpace(task))
            return new ActionResult("failure", "Agent action requires 'task'");

        var request = new AgentExecutionRequest(
            stage,
            task,
            changeDir,
            context.WorkDir,
            BuildPrompt(stage, task, changeDir),
            context.CancellationToken);

        var result = await _executor.ExecuteAsync(request);
        var output = JsonSerializer.Serialize(new
        {
            kind = "agent",
            stage,
            task,
            changeDir,
            result.Stdout,
            result.Stderr,
        });

        return result.ExitCode == 0
            ? new ActionResult("success", $"Agent task completed: {stage}/{task}", output, result.ExitCode)
            : new ActionResult("failure", result.Stderr ?? result.Stdout ?? $"Agent exited with code {result.ExitCode}", output, result.ExitCode);
    }

    private static string? ResolveChangeDir(ActionContext context)
    {
        var changeDir = JsonInputs.String(context.With, "changeDir");
        if (string.IsNullOrWhiteSpace(changeDir)) return null;
        return Path.IsPathRooted(changeDir) ? changeDir : Path.GetFullPath(Path.Combine(context.WorkDir, changeDir));
    }

    private static string BuildPrompt(string stage, string task, string? changeDir)
    {
        var artifactInstruction = changeDir is null
            ? "No change artifact directory was provided."
            : $"Use this change artifact directory for stage outputs: {changeDir}";

        return $$"""
        You are running a Mohist workflow task.

        Stage: {{stage}}
        Task: {{task}}

        {{artifactInstruction}}

        Complete only this workflow task. Keep changes scoped to the current workspace.
        """;
    }
}

public interface IAgentExecutor
{
    Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request);
}

public sealed record AgentExecutionRequest(
    string Stage,
    string Task,
    string? ChangeDir,
    string WorkDir,
    string Prompt,
    CancellationToken CancellationToken);

public sealed record AgentExecutionResult(int ExitCode, string? Stdout = null, string? Stderr = null);

public class ProcessAgentExecutor : IAgentExecutor
{
    private readonly string _command;
    private readonly ILogger<ProcessAgentExecutor> _log;

    public ProcessAgentExecutor(ILogger<ProcessAgentExecutor> log, string command = "opencode")
    {
        _log = log;
        _command = command;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _command,
            WorkingDirectory = request.WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("agent");
        process.StartInfo.ArgumentList.Add("--local");
        process.StartInfo.ArgumentList.Add("--message");
        process.StartInfo.ArgumentList.Add(request.Prompt);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        _log.LogInformation("Running agent task {Stage}/{Task} in {WorkDir}", request.Stage, request.Task, request.WorkDir);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(request.CancellationToken);

        return new AgentExecutionResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
