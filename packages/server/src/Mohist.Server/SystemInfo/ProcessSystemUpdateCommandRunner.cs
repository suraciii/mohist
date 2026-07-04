using System.Diagnostics;

namespace Mohist.Server.SystemInfo;

public interface ISystemUpdateCommandRunner
{
    Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default);
}

public sealed record SystemCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string Stage,
    int MaxOutputBytes = 8192);

public sealed record SystemCommandResult(int ExitCode, string Output);

public sealed class ProcessSystemUpdateCommandRunner : ISystemUpdateCommandRunner
{
    public async Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        if (process == null)
            return new SystemCommandResult(-1, $"Failed to start {command.FileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdoutTask) + (await stderrTask);

        if (output.Length > command.MaxOutputBytes)
            output = output[..command.MaxOutputBytes];

        return new SystemCommandResult(process.ExitCode, output.Trim());
    }
}