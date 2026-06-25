namespace Mohist.Cli;

internal sealed partial class SourceCodeUpdater
{
    public async Task<int> UpdateCliAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        return await _operations.UpdateCliAsync(repoRoot, dryRun, cliPath, cancellationToken);
    }

    public async Task<int> UpdateServerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        return await _operations.UpdateServerAsync(repoRoot, dryRun, _serverReadyTimeout, _readinessProbe, cancellationToken);
    }

    public async Task<int> UpdateRunnerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        return await _operations.UpdateRunnerAsync(repoRoot, dryRun, _runnerRefreshVerifier, cancellationToken);
    }
}
