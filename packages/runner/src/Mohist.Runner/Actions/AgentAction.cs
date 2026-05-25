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
        var requirements = AgentCompletionRequirements.From(context.With, context.WorkDir);

        if (string.IsNullOrWhiteSpace(stage))
            return new ActionResult("failure", "Agent action requires 'stage'");
        if (string.IsNullOrWhiteSpace(task))
            return new ActionResult("failure", "Agent action requires 'task'");

        var prompt = AgentPromptRenderer.Render(new AgentPromptContext(stage, task, requirements, context.WorkDir, context.Variables));
        var model = AgentPromptRenderer.ResolveModel(context.Variables, stage);

        var request = new AgentExecutionRequest(
            stage,
            task,
            requirements.ChangeDir,
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
            changeDir = requirements.ChangeDir,
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

}

public sealed record AgentCompletionRequirements(
    IReadOnlyList<RequiredFile> Files,
    IReadOnlyList<RequiredMarker> Markers)
{
    public string? ChangeDir => Files.Concat(Markers.Select(m => new RequiredFile(m.Path)))
        .Select(r => Path.GetDirectoryName(r.Path))
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .OrderBy(path => path!.Length)
        .FirstOrDefault();

    public static AgentCompletionRequirements From(Dictionary<string, JsonElement?>? with, string workDir)
    {
        var files = ReadArray(with, "requireFiles")
            .Select(item => ReadPath(item, workDir))
            .Where(path => path is not null)
            .Select(path => new RequiredFile(path!))
            .ToList();
        var markers = ReadArray(with, "requireMarkers")
            .Select(item => ReadMarker(item, workDir))
            .Where(marker => marker is not null)
            .Cast<RequiredMarker>()
            .ToList();
        return new AgentCompletionRequirements(files, markers);
    }

    public static AgentCompletionRequirements Empty { get; } = new([], []);

    private static IEnumerable<JsonElement> ReadArray(Dictionary<string, JsonElement?>? with, string name)
    {
        if (with is null || !with.TryGetValue(name, out var element) || element?.ValueKind != JsonValueKind.Array)
            return [];
        return element.Value.EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private static string? ReadPath(JsonElement item, string workDir)
    {
        if (!item.TryGetProperty("path", out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var path = value.GetString();
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workDir, path));
    }

    private static RequiredMarker? ReadMarker(JsonElement item, string workDir)
    {
        var path = ReadPath(item, workDir);
        if (path is null) return null;
        if (!item.TryGetProperty("marker", out var markerValue) || markerValue.ValueKind != JsonValueKind.String)
            return null;
        var marker = markerValue.GetString();
        return string.IsNullOrWhiteSpace(marker) ? null : new RequiredMarker(path, marker!);
    }
}

public sealed record RequiredFile(string Path);
public sealed record RequiredMarker(string Path, string Marker);

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
