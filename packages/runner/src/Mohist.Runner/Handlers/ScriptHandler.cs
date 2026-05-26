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
        var shell = JsonInputs.String(context.With, "shell") ?? DefaultShell();
        var script = JsonInputs.String(context.With, "run") ?? "";

        if (string.IsNullOrWhiteSpace(script))
            return new ActionResult("failure", "Script action requires 'run'");

        var scriptFile = Path.Combine(context.WorkDir, $"_{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptFile, script);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (JsonInputs.Int(context.With, "timeout") is { } timeoutMs)
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

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

            await process.WaitForExitAsync(timeout.Token);

            var output = JsonSerializer.Serialize(new
            {
                kind = "script",
                run = script,
                shell,
                process.ExitCode,
                stdout = Trim(stdout.ToString()),
                stderr = Trim(stderr.ToString()),
            });

            return process.ExitCode == 0
                ? new ActionResult("success", "Script completed", output, process.ExitCode)
                : new ActionResult("failure", $"Script failed: {FirstLine(script)}", output, process.ExitCode);
        }
        finally
        {
            if (File.Exists(scriptFile))
                File.Delete(scriptFile);
        }
    }

    private static string DefaultShell() =>
        OperatingSystem.IsWindows() ? "pwsh" : "bash";

    private static string Trim(string value) => value.Length <= 20_000 ? value : value[..20_000];

    private static string FirstLine(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var index = normalized.IndexOf('\n');
        return index < 0 ? normalized : normalized[..index];
    }
}
