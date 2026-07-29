namespace Mohist.Cli;

internal partial class SourceCodeUpdater
{
    public virtual async Task<int> UpdateCliAsync(string? repoRoot, bool dryRun, string? cliPath = null, CancellationToken cancellationToken = default)
    {
        return await _operations.UpdateCliAsync(repoRoot, dryRun, cliPath, cancellationToken);
    }

    public virtual async Task<int> UpdateServerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        return await _operations.UpdateServerAsync(repoRoot, dryRun, _serverReadyTimeout, _readinessProbe, cancellationToken);
    }

    public virtual async Task<int> UpdateRunnerAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        return await _operations.UpdateRunnerAsync(repoRoot, dryRun, _runnerRefreshVerifier, cancellationToken);
    }

    public virtual async Task<int> UpdateSlackAsync(string? repoRoot, bool dryRun, CancellationToken cancellationToken = default)
    {
        return await _operations.UpdateSlackAsync(repoRoot, dryRun, cancellationToken);
    }
}
