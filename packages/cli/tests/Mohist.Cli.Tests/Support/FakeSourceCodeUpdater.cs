namespace Mohist.Cli.Tests.Support;

/// <summary>
/// Recording fake for <see cref="SourceCodeUpdater"/>. Each entry-point
/// records the call and returns 0 without touching source builds, services,
/// or the runner — keeping <c>mo update*</c> specs hermetic per
/// <c>design/testing.md</c>.
/// </summary>
internal sealed class FakeSourceCodeUpdater : SourceCodeUpdater
{
    public List<string> Calls { get; } = new();
    public List<ServiceInstallOptions> ServerInstallOptions { get; } = new();
    public List<ServiceInstallOptions> RunnerInstallOptions { get; } = new();

    public FakeSourceCodeUpdater()
        : base(
            output: new StringWriter(),
            error: new StringWriter(),
            operations: null!,
            validator: null!,
            readinessProbe: null!,
            runnerRefreshVerifier: null!,
            outcomeReporter: null!)
    {
    }

    public override Task<int> UpdateAllAsync(
        string? repoRoot,
        bool dryRun,
        string? cliPath = null,
        CancellationToken cancellationToken = default,
        bool continueAfterCliUpdate = false)
    {
        Calls.Add(nameof(UpdateAllAsync));
        return Task.FromResult(0);
    }

    public override Task<int> UpdateCliAsync(
        string? repoRoot,
        bool dryRun,
        string? cliPath = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(UpdateCliAsync));
        return Task.FromResult(0);
    }

    public override Task<int> UpdateServerAsync(
        string? repoRoot,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(UpdateServerAsync));
        return Task.FromResult(0);
    }

    public override Task<int> UpdateRunnerAsync(
        string? repoRoot,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(UpdateRunnerAsync));
        return Task.FromResult(0);
    }

    public override Task<int> UpdateSlackAsync(
        string? repoRoot,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(UpdateSlackAsync));
        return Task.FromResult(0);
    }

    public override Task<int> InstallServerAsync(ServiceInstallOptions options, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(InstallServerAsync));
        ServerInstallOptions.Add(options);
        return Task.FromResult(0);
    }

    public override Task<int> InstallRunnerAsync(ServiceInstallOptions options, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(InstallRunnerAsync));
        RunnerInstallOptions.Add(options);
        return Task.FromResult(0);
    }
}
