namespace Mohist.Cli;

internal interface IManagedRuntimeActivator
{
    Task<int> ApplyManagedRuntimeAsync(
        RuntimeTargetSet targets,
        string scope,
        string? unitDir,
        CancellationToken cancellationToken = default);

    Task<int> RestoreManagedRuntimeAsync(
        RuntimeTargetSet? targets,
        string scope,
        string? unitDir,
        CancellationToken cancellationToken = default);
}

internal interface IServiceInstaller
{
    Task<int> InstallServerAsync(ServiceInstallOptions options);
    Task<int> InstallRunnerAsync(ServiceInstallOptions options);
    Task<int> InstallSlackAsync(ServiceInstallOptions options);

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

    Task<int> StartSlackAsync(ServiceCommandOptions options);
    Task<int> StopSlackAsync(ServiceCommandOptions options);
    Task<int> RestartSlackAsync(ServiceCommandOptions options);
    Task<int> StatusSlackAsync(ServiceCommandOptions options);
    Task<int> LogsSlackAsync(ServiceCommandOptions options);
    Task<int> UninstallSlackAsync(ServiceCommandOptions options);

    Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes whether the runner service is installed/manageable in the current environment.
    /// Returns <c>true</c> when the service unit/task is present, <c>false</c> otherwise.
    /// Implementations MUST be side-effect free (no systemctl calls that change state).
    /// </summary>
    /// <param name="unitDir">
    /// Optional override for the systemd user unit directory. Implementations on non-systemd
    /// platforms ignore this value. <c>null</c> selects the platform-default location.
    /// </param>
    Task<bool> IsRunnerInstalledAsync(string? unitDir = null);
    Task<bool> IsSlackInstalledAsync(string? unitDir = null);
}
