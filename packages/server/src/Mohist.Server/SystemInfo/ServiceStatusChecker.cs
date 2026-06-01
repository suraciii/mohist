using System.Diagnostics;

namespace Mohist.Server.SystemInfo;

public interface IServiceStatusChecker
{
    Task<string?> GetStatusAsync(string? unitName);
}

public sealed class SystemdServiceStatusChecker : IServiceStatusChecker
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public async Task<string?> GetStatusAsync(string? unitName)
    {
        if (string.IsNullOrWhiteSpace(unitName))
            return null;

        try
        {
            var psi = new ProcessStartInfo("systemctl", ["--user", "is-active", unitName])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var status = output.Trim();
            return string.IsNullOrWhiteSpace(status) ? null : status;
        }
        catch
        {
            return null;
        }
    }
}
