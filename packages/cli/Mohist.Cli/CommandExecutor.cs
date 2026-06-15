using System.Diagnostics;

namespace Mohist.Cli;

internal interface ICommandExecutor
{
    Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default);
}

internal sealed class SystemCommandExecutor : ICommandExecutor
{
    public async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workingDirectory != null)
            process.StartInfo.WorkingDirectory = workingDirectory;
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (1, "", $"Failed to run {fileName}: {ex.Message}");
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            });
        }

        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            return (-1, "", "");
        }
    }
}
