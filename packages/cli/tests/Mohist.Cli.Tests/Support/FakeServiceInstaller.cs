namespace Mohist.Cli.Tests.Support;

/// <summary>
/// No-op <see cref="IServiceInstaller"/> for tests that need to supply an
/// installer collaborator without exercising real service management.
/// Every method succeeds with exit code 0; runner probes report absent.
/// Every invocation is also recorded so specs can assert call/no-call
/// behavior of deleted or surviving command paths.
/// </summary>
internal sealed class FakeServiceInstaller : IServiceInstaller
{
    public bool RunnerInstalled { get; set; }
    public List<string> Calls { get; } = new();
    public List<ServiceInstallOptions> InstallServerCalls { get; } = new();
    public List<ServiceInstallOptions> InstallRunnerCalls { get; } = new();
    public List<ServiceInstallOptions> InstallSlackCalls { get; } = new();

    public Task<int> InstallServerAsync(ServiceInstallOptions options)
    {
        Calls.Add(nameof(InstallServerAsync));
        InstallServerCalls.Add(options);
        return Task.FromResult(0);
    }

    public Task<int> InstallRunnerAsync(ServiceInstallOptions options)
    {
        Calls.Add(nameof(InstallRunnerAsync));
        InstallRunnerCalls.Add(options);
        return Task.FromResult(0);
    }

    public Task<int> InstallSlackAsync(ServiceInstallOptions options)
    {
        Calls.Add(nameof(InstallSlackAsync));
        InstallSlackCalls.Add(options);
        return Task.FromResult(0);
    }

    public Task<int> StartServerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(StartServerAsync));
        return Task.FromResult(0);
    }

    public Task<int> StopServerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(StopServerAsync));
        return Task.FromResult(0);
    }

    public Task<int> RestartServerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(RestartServerAsync));
        return Task.FromResult(0);
    }

    public Task<int> StatusServerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(StatusServerAsync));
        return Task.FromResult(0);
    }

    public Task<int> LogsServerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(LogsServerAsync));
        return Task.FromResult(0);
    }

    public Task<int> UninstallServerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(UninstallServerAsync));
        return Task.FromResult(0);
    }

    public Task<int> StartRunnerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(StartRunnerAsync));
        return Task.FromResult(0);
    }

    public Task<int> StopRunnerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(StopRunnerAsync));
        return Task.FromResult(0);
    }

    public Task<int> RestartRunnerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(RestartRunnerAsync));
        return Task.FromResult(0);
    }

    public Task<int> StatusRunnerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(StatusRunnerAsync));
        return Task.FromResult(0);
    }

    public Task<int> LogsRunnerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(LogsRunnerAsync));
        return Task.FromResult(0);
    }

    public Task<int> UninstallRunnerAsync(ServiceCommandOptions options)
    {
        Calls.Add(nameof(UninstallRunnerAsync));
        return Task.FromResult(0);
    }

    public Task<int> StartSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(StartSlackAsync)); return Task.FromResult(0); }
    public Task<int> StopSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(StopSlackAsync)); return Task.FromResult(0); }
    public Task<int> RestartSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(RestartSlackAsync)); return Task.FromResult(0); }
    public Task<int> StatusSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(StatusSlackAsync)); return Task.FromResult(0); }
    public Task<int> LogsSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(LogsSlackAsync)); return Task.FromResult(0); }
    public Task<int> UninstallSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(UninstallSlackAsync)); return Task.FromResult(0); }

    public Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> IsRunnerInstalledAsync(string? unitDir = null) => Task.FromResult(RunnerInstalled);
    public Task<bool> IsSlackInstalledAsync(string? unitDir = null) => Task.FromResult(false);
}
