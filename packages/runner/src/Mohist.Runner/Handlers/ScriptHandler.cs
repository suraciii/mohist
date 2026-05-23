using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mohist.Runner.Actions;

namespace Mohist.Runner.Handlers;

public class ScriptHandler : IAction
{
    private readonly ILogger<ScriptHandler> _log;

    public ScriptHandler(ILogger<ScriptHandler> log)
    {
        _log = log;
    }

    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var shell = context.With?.TryGetValue("shell", out var shellVal) == true
            ? shellVal?.GetString() ?? DefaultShell()
            : DefaultShell();

        var script = context.With?.TryGetValue("run", out var runVal) == true
            ? runVal?.GetString() ?? ""
            : "";

        if (string.IsNullOrWhiteSpace(script))
            return new ActionResult("failed", "ScriptHandler requires 'run' input");

        var scriptFile = Path.Combine(context.WorkDir, $"_{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptFile, script);

        try
        {
            _log.LogInformation("Running script in {WorkDir} with {Shell}", context.WorkDir, shell);

            using var process = new System.Diagnostics.Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"\"{scriptFile}\"",
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
                    if (kv.Key == "run" || kv.Key == "shell") continue;
                    if (kv.Value is not null)
                        process.StartInfo.Environment[$"INPUT_{kv.Key.ToUpperInvariant()}"] = kv.Value.ToString();
                }
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(context.CancellationToken);

            return process.ExitCode == 0
                ? new ActionResult("completed", stdout.ToString().Trim())
                : new ActionResult("failed", stderr.Length > 0 ? stderr.ToString().Trim() : $"Exit code {process.ExitCode}", ExitCode: process.ExitCode);
        }
        finally
        {
            if (File.Exists(scriptFile))
                File.Delete(scriptFile);
        }
    }

    private static string DefaultShell() =>
        OperatingSystem.IsWindows() ? "pwsh" : "bash";
}
