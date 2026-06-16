namespace Mohist.Cli;

internal interface IServiceInstaller
{
    Task<int> InstallServerAsync(ServiceInstallOptions options);
    Task<int> InstallRunnerAsync(ServiceInstallOptions options);

    Task<int> StartServerAsync(ServiceCommandOptions options);
    Task<int> StopServerAsync(ServiceCommandOptions options);
    Task<int> RestartServerAsync(ServiceCommandOptions options);
    Task<int> StatusServerAsync(ServiceCommandOptions options);
    Task<int> LogsServerAsync(ServiceCommandOptions options);
    Task<int> UninstallServerAsync(ServiceCommandOptions options);

    Task<int> StartRunnerAsync(ServiceCommandOptions options);
    Task<int> StopRunnerAsync(ServiceCommandOptions options);
    Task<int> RestartRunnerAsync(ServiceCommandOptions options);
    Task<int> StatusRunnerAsync(ServiceCommandOptions options);
    Task<int> LogsRunnerAsync(ServiceCommandOptions options);
    Task<int> UninstallRunnerAsync(ServiceCommandOptions options);

    Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default);
}
