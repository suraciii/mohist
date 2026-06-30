namespace Mohist.Cli.Tests.Support;

/// <summary>
/// No-op <see cref="IServiceInstaller"/> for tests that need to supply an
/// installer collaborator without exercising real service management.
/// Every method succeeds with exit code 0; runner probes report absent.
/// </summary>
internal sealed class FakeServiceInstaller : IServiceInstaller
{
    public Task<int> InstallServerAsync(ServiceInstallOptions options) => Task.FromResult(0);
    public Task<int> InstallRunnerAsync(ServiceInstallOptions options) => Task.FromResult(0);
    public Task<int> StartServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> StopServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> RestartServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> StatusServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> LogsServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> UninstallServerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> StartRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> StopRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> RestartRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> StatusRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> LogsRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<int> UninstallRunnerAsync(ServiceCommandOptions options) => Task.FromResult(0);
    public Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> IsRunnerInstalledAsync(string? unitDir = null) => Task.FromResult(false);
}
