using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mohist.Runner.Actions;

public class HealthGateAction : IAction
{
    private readonly ILogger<HealthGateAction> _log;

    public HealthGateAction(ILogger<HealthGateAction> log)
    {
        _log = log;
    }

    public async Task<ActionResult> ExecuteAsync(ActionContext context)
    {
        var command = JsonInputs.String(context.With, "command");
        if (string.IsNullOrWhiteSpace(command))
            return new ActionResult("failure", "Health gate requires 'command'");

        var timeoutMs = JsonInputs.Int(context.With, "timeout") ?? 300_000;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        var (fileName, arguments) = Shell(command);
        var result = await RunAsync(context.WorkDir, fileName, arguments, timeout.Token);

        var output = JsonSerializer.Serialize(new
        {
            kind = "health-gate",
            command,
            result.ExitCode,
            stdout = Trim(result.Stdout),
            stderr = Trim(result.Stderr),
        });

        return result.ExitCode == 0
            ? new ActionResult("success", "Health gate passed", output, result.ExitCode)
            : new ActionResult("failure", $"Health gate failed: {command}", output, result.ExitCode);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string workDir, string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        _log.LogInformation("Running health gate: {FileName} {Arguments}", fileName, arguments);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static (string FileName, string Arguments) Shell(string command) => OperatingSystem.IsWindows()
        ? ("pwsh", $"-NoLogo -NoProfile -Command \"{command.Replace("\"", "\\\"")}\"")
        : ("sh", $"-c \"{command.Replace("\"", "\\\"")}\"");

    private static string Trim(string value) => value.Length <= 20_000 ? value : value[..20_000];
}
