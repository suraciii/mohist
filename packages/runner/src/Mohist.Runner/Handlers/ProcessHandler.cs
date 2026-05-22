using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mohist.Runner.Actions;

namespace Mohist.Runner.Handlers;

public class ProcessHandler : IAction
{
    private readonly ILogger<ProcessHandler> _log;

    public ProcessHandler(ILogger<ProcessHandler> log)
    {
        _log = log;
    }

    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var command = context.Uses ?? throw new ArgumentException("ProcessHandler requires 'uses' as command");
        var args = BuildArgs(context.With);

        _log.LogInformation("Executing: {Command} {Args} in {WorkDir}", command, args, context.WorkDir);

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = context.WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (context.With is not null)
        {
            foreach (var kv in context.With)
            {
                if (kv.Value is not null)
                    process.StartInfo.Environment[$"INPUT_{kv.Key.ToUpperInvariant()}"] = kv.Value.ToString();
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); _log.LogDebug("[stdout] {Data}", e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); _log.LogWarning("[stderr] {Data}", e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        var exitCode = process.ExitCode;
        var message = stderr.Length > 0 ? stderr.ToString().Trim() : null;

        _log.LogInformation("Process exited with code {ExitCode}", exitCode);

        return exitCode == 0
            ? new ActionResult("completed", stdout.ToString().Trim())
            : new ActionResult("failed", message ?? $"Process exited with code {exitCode}", ExitCode: exitCode);
    }

    private static string BuildArgs(Dictionary<string, JsonElement?>? with)
    {
        if (with is null) return "";
        var parts = new List<string>();
        foreach (var kv in with)
        {
            if (kv.Value is not null)
                parts.Add(kv.Value.ToString());
        }
        return string.Join(" ", parts);
    }
}
