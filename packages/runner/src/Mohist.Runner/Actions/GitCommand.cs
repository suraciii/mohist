using System.Diagnostics;
using System.Text;

namespace Mohist.Runner.Actions;

internal static class GitCommand
{
    public static async Task<GitCommandResult> RunAsync(string workDir, string[] args, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return new GitCommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

internal sealed record GitCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
    public string CombinedOutput => string.Join("\n", new[] { Stdout.Trim(), Stderr.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
}
