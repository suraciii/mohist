using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mohist.Runner.Transport;

namespace Mohist.Runner.Actions;

public class AgentAction : IAction
{
    private readonly IAgentExecutor _executor;
    private readonly ISessionTelemetrySink _telemetry;

    public AgentAction(IAgentExecutor executor, ISessionTelemetrySink? telemetry = null)
    {
        _executor = executor;
        _telemetry = telemetry ?? new NullSessionTelemetrySink();
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

        var prompt = AgentPromptRenderer.Render(new AgentPromptContext(stage, task, changeDir, context.WorkDir, context.Variables));
        var model = AgentPromptRenderer.ResolveModel(context.Variables, stage);

        var request = new AgentExecutionRequest(
            stage,
            task,
            changeDir,
            context.WorkDir,
            prompt,
            model,
            context.Session,
            _telemetry,
            context.CancellationToken);

        if (context.Session is not null)
            await _telemetry.AppendAsync(context.Session, [new SessionEventInput(
                "mohist_prompt",
                JsonSerializer.SerializeToElement(new
                {
                    text = prompt,
                    sentAt = DateTime.UtcNow.ToString("o"),
                    kind = "task",
                    issueId = context.Session.IssueNumber.ToString(),
                    acpSessionId = context.Session.ExternalSessionId ?? context.Session.Id,
                }))], context.CancellationToken);

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

        var status = result.ExitCode == 0 ? "completed" : "failed";
        if (context.Session is not null)
            await _telemetry.CompletedAsync(context.Session, new SessionCompleted(status, result.Stderr, result.ExitCode), context.CancellationToken);

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

    private static string BuildPrompt(string stage, string task, string? changeDir) =>
        AgentPromptRenderer.Render(new AgentPromptContext(stage, task, changeDir, Directory.GetCurrentDirectory(), null));
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
    string? Model,
    AgentSessionContext? Session,
    ISessionTelemetrySink Telemetry,
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
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            process.StartInfo.ArgumentList.Add("--model");
            process.StartInfo.ArgumentList.Add(request.Model);
        }
        process.StartInfo.ArgumentList.Add(request.Prompt);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            if (request.Session is not null)
                _ = request.Telemetry.AppendAsync(request.Session, [new SessionEventInput("agent_message_chunk", JsonSerializer.SerializeToElement(new { text = e.Data + Environment.NewLine }))], request.CancellationToken);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            if (request.Session is not null)
                _ = request.Telemetry.AppendAsync(request.Session, [new SessionEventInput("agent_output_chunk", JsonSerializer.SerializeToElement(new { text = e.Data + Environment.NewLine, stream = "stderr" }))], request.CancellationToken);
        };

        _log.LogInformation("Running agent task {Stage}/{Task} in {WorkDir}", request.Stage, request.Task, request.WorkDir);
        process.Start();
        if (request.Session is not null)
            await request.Telemetry.StartedAsync(request.Session, new SessionStarted(
                ExternalSessionId: request.Session.ExternalSessionId ?? request.Session.Id,
                WorkDir: request.WorkDir,
                ChangeDir: request.ChangeDir,
                ProcessPid: process.Id), request.CancellationToken);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(request.CancellationToken);

        return new AgentExecutionResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

}
