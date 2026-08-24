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
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (OperatingSystem.IsWindows()) process.StartInfo.CreateNewProcessGroup = true;
        var processTreeId = Guid.NewGuid().ToString("N");
        process.StartInfo.Environment[CommandProcessTree.EnvironmentVariable] = processTreeId;
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var descendants = new Dictionary<int, Process>();
            var terminationFailures = new List<Exception>();
            void Track(IEnumerable<Process> processes)
            {
                foreach (var descendant in processes)
                {
                    if (!descendants.TryAdd(descendant.Id, descendant)) descendant.Dispose();
                }
            }
            try
            {
                try
                {
                    Track(CommandProcessTree.CaptureDescendants(process.Id));
                }
                catch (Exception ex)
                {
                    terminationFailures.Add(ex);
                }
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
                catch (Exception ex)
                {
                    terminationFailures.Add(ex);
                }
                try
                {
                    Track(CommandProcessTree.CaptureRemainingDescendants(process.Id, processTreeId));
                }
                catch (Exception ex)
                {
                    terminationFailures.Add(ex);
                }
                foreach (var descendant in descendants.Values)
                {
                    try
                    {
                        if (!descendant.HasExited)
                            descendant.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (descendant.HasExited)
                    {
                    }
                    catch (Exception ex)
                    {
                        terminationFailures.Add(ex);
                    }
                }
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    terminationFailures.Add(ex);
                }
                foreach (var descendant in descendants.Values)
                {
                    try
                    {
                        await descendant.WaitForExitAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        terminationFailures.Add(ex);
                    }
                }
            }
            finally
            {
                foreach (var descendant in descendants.Values) descendant.Dispose();
            }
            if (terminationFailures.Count > 0)
                throw new IOException(
                    $"Failed to terminate cancelled command {fileName}.",
                    new AggregateException(terminationFailures));
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch
            {
            }
            throw;
        }
    }
}
