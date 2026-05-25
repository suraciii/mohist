using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mohist.Runner.Transport;

namespace Mohist.Runner.Actions;

public class AgentAction : IAction
{
    private readonly IAgentExecutor _executor;
    private readonly ISessionTelemetrySink _telemetry;
    private readonly IAgentCompletionVerifier _completionVerifier;
    private readonly IAgentSessionRepairer _repairer;

    public AgentAction(
        IAgentExecutor executor,
        ISessionTelemetrySink? telemetry = null,
        IAgentCompletionVerifier? completionVerifier = null,
        IAgentSessionRepairer? repairer = null)
    {
        _executor = executor;
        _telemetry = telemetry ?? new NullSessionTelemetrySink();
        _completionVerifier = completionVerifier ?? new AgentCompletionVerifier();
        _repairer = repairer ?? new NoopAgentSessionRepairer();
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

        var prompt = AgentPromptRenderer.Render(new AgentPromptContext(stage, task, requirements, context.WorkDir, context.Variables, context.With));
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

        var status = result.ExitCode == 0 ? "completed" : "failed";
        var verification = status == "completed"
            ? await _completionVerifier.VerifyAsync(requirements, context.CancellationToken)
            : AgentCompletionVerificationResult.Success;

        if (status == "completed" && !verification.Satisfied)
        {
            var repair = await _repairer.RepairAsync(request, verification, context.CancellationToken);
            if (repair.Attempted)
            {
                result = repair.Result ?? result;
                status = result.ExitCode == 0 ? "completed" : "failed";
                verification = status == "completed"
                    ? await _completionVerifier.VerifyAsync(requirements, context.CancellationToken)
                    : verification;
            }
        }

        var output = JsonSerializer.Serialize(new
        {
            kind = "agent",
            stage,
            task,
            changeDir = requirements.ChangeDir,
            result.Stdout,
            result.Stderr,
            completion = verification.ToOutput(),
        });

        if (status == "completed" && !verification.Satisfied)
            status = "failed";

        if (context.Session is not null)
            await _telemetry.CompletedAsync(context.Session, new SessionCompleted(status, CompletionMessage(result, verification), result.ExitCode), context.CancellationToken);

        return status == "completed"
            ? new ActionResult("success", $"Agent task completed: {stage}/{task}", output, result.ExitCode)
            : new ActionResult("failure", CompletionMessage(result, verification), output, result.ExitCode);
    }

    private static string CompletionMessage(AgentExecutionResult result, AgentCompletionVerificationResult verification)
    {
        if (!verification.Satisfied)
            return verification.Message;
        return result.Stderr ?? result.Stdout ?? $"Agent exited with code {result.ExitCode}";
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

public interface IAgentCompletionVerifier
{
    Task<AgentCompletionVerificationResult> VerifyAsync(AgentCompletionRequirements requirements, CancellationToken ct);
}

public sealed record MissingRequiredFile(string Path);
public sealed record MissingRequiredMarker(string Path, string Marker);

public sealed record AgentCompletionVerificationResult(
    IReadOnlyList<MissingRequiredFile> MissingFiles,
    IReadOnlyList<MissingRequiredMarker> MissingMarkers)
{
    public static AgentCompletionVerificationResult Success { get; } = new([], []);

    public bool Satisfied => MissingFiles.Count == 0 && MissingMarkers.Count == 0;

    public string Message
    {
        get
        {
            if (Satisfied) return "Agent completion requirements satisfied";
            var parts = new List<string>();
            parts.AddRange(MissingFiles.Select(file => $"missing file: {file.Path}"));
            parts.AddRange(MissingMarkers.Select(marker => $"missing marker in {marker.Path}: {marker.Marker}"));
            return "Agent completion requirements were not satisfied: " + string.Join("; ", parts);
        }
    }

    public object ToOutput() => new
    {
        satisfied = Satisfied,
        missingFiles = MissingFiles,
        missingMarkers = MissingMarkers,
    };
}

public class AgentCompletionVerifier : IAgentCompletionVerifier
{
    public async Task<AgentCompletionVerificationResult> VerifyAsync(AgentCompletionRequirements requirements, CancellationToken ct)
    {
        var missingFiles = requirements.Files
            .Where(file => !File.Exists(file.Path) && !Directory.Exists(file.Path))
            .Select(file => new MissingRequiredFile(file.Path))
            .ToList();

        var missingMarkers = new List<MissingRequiredMarker>();
        foreach (var marker in requirements.Markers)
        {
            if (!File.Exists(marker.Path))
            {
                missingMarkers.Add(new MissingRequiredMarker(marker.Path, marker.Marker));
                continue;
            }

            var content = await File.ReadAllTextAsync(marker.Path, ct);
            if (!content.Contains(marker.Marker, StringComparison.Ordinal))
                missingMarkers.Add(new MissingRequiredMarker(marker.Path, marker.Marker));
        }

        return new AgentCompletionVerificationResult(missingFiles, missingMarkers);
    }
}

public interface IAgentSessionRepairer
{
    Task<AgentRepairResult> RepairAsync(AgentExecutionRequest request, AgentCompletionVerificationResult verification, CancellationToken ct);
}

public sealed record AgentRepairResult(bool Attempted, AgentExecutionResult? Result = null);

public sealed class NoopAgentSessionRepairer : IAgentSessionRepairer
{
    public Task<AgentRepairResult> RepairAsync(AgentExecutionRequest request, AgentCompletionVerificationResult verification, CancellationToken ct) =>
        Task.FromResult(new AgentRepairResult(false));
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
