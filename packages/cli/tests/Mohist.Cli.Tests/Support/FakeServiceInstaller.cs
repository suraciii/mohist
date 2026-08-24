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
    public string SlackBinaryName { get; set; } = OperatingSystem.IsWindows() ? "mohist-slack.exe" : "mohist-slack";

    public bool RunnerInstalled { get; set; }
    public bool SlackInstalled { get; set; }
    public int RestartRunnerResult { get; set; }
    public int RestartSlackResult { get; set; }
    public int StopSlackResult { get; set; }
    public int RefreshSlackResult { get; set; }
    public Queue<int> RefreshSlackResults { get; } = new();
    public int StartSlackResult { get; set; }
    public Queue<int> StartSlackResults { get; } = new();
    public int RestoreSlackResult { get; set; }
    public bool SlackRunning { get; set; } = true;
    public Queue<bool> SlackRunningResults { get; } = new();
    public Func<bool>? SlackRunningProbe { get; set; }
    public Action? SlackLifecycleAction { get; set; }
    public Action? StopSlackAction { get; set; }
    public List<string> Calls { get; } = new();
    public List<CancellationToken> SlackLifecycleCancellationTokens { get; } = new();
    public List<ServiceInstallOptions> InstallServerCalls { get; } = new();
    public List<ServiceInstallOptions> InstallRunnerCalls { get; } = new();
    public List<ServiceInstallOptions> InstallSlackCalls { get; } = new();
    public List<string> RefreshSlackRoots { get; } = new();
    public SlackServiceSnapshot? SlackSnapshot { get; set; } = new("fake", "/launcher", "launcher");

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

    public Task<int> RefreshSlackServiceAsync(
        string repoRoot,
        string? unitDir = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(RefreshSlackServiceAsync));
        RefreshSlackRoots.Add(repoRoot);
        return Task.FromResult(
            RefreshSlackResults.Count > 0 ? RefreshSlackResults.Dequeue() : RefreshSlackResult);
    }

    public Task<SlackServiceSnapshot?> CaptureSlackServiceAsync(
        string? unitDir = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(CaptureSlackServiceAsync));
        return Task.FromResult<SlackServiceSnapshot?>(SlackSnapshot);
    }

    public Task<int> RestoreSlackServiceAsync(
        SlackServiceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(RestoreSlackServiceAsync));
        return Task.FromResult(RestoreSlackResult);
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
        return Task.FromResult(RestartRunnerResult);
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

    public Task<int> StartSlackAsync(ServiceCommandOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SlackLifecycleCancellationTokens.Add(cancellationToken);
        SlackLifecycleAction?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(nameof(StartSlackAsync));
        var result = StartSlackResults.Count > 0 ? StartSlackResults.Dequeue() : StartSlackResult;
        if (result == 0) SlackRunning = true;
        return Task.FromResult(result);
    }
    public Task<int> StopSlackAsync(ServiceCommandOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SlackLifecycleCancellationTokens.Add(cancellationToken);
        SlackLifecycleAction?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(nameof(StopSlackAsync));
        StopSlackAction?.Invoke();
        if (StopSlackResult == 0) SlackRunning = false;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StopSlackResult);
    }
    public Task<int> RestartSlackAsync(ServiceCommandOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SlackLifecycleCancellationTokens.Add(cancellationToken);
        SlackLifecycleAction?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(nameof(RestartSlackAsync));
        return Task.FromResult(RestartSlackResult);
    }
    public Task<int> StatusSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(StatusSlackAsync)); return Task.FromResult(0); }
    public Task<int> LogsSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(LogsSlackAsync)); return Task.FromResult(0); }
    public Task<int> UninstallSlackAsync(ServiceCommandOptions options) { Calls.Add(nameof(UninstallSlackAsync)); return Task.FromResult(0); }

    public Task<bool> IsRunnerRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> IsSlackRunningAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(IsSlackRunningAsync));
        return Task.FromResult(
            SlackRunningProbe?.Invoke()
            ?? (SlackRunningResults.Count > 0 ? SlackRunningResults.Dequeue() : SlackRunning));
    }
    public Task<bool> IsRunnerInstalledAsync(string? unitDir = null) => Task.FromResult(RunnerInstalled);
    public Task<bool> IsSlackInstalledAsync(string? unitDir = null) => Task.FromResult(SlackInstalled);
}
