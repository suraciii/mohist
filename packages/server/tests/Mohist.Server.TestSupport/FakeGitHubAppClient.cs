using Mohist.Server.GitHub.Ports;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Deterministic GitHub App seam for server specs. It returns verified
/// installation facts without making a network request.
/// </summary>
public sealed class FakeGitHubAppClient : IGitHubAppClient
{
    private int _repositorySequence;
    public string InstallationId { get; set; } = "installation-test";
    public string RepositoryNodeId { get; set; } = "repository-node-test";
    public bool InstallationMissing { get; set; }
    public Exception? DiscoveryFailure { get; set; }

    public Task<GitHubRepositoryInstallation> DiscoverInstallationAsync(string owner, string repo, CancellationToken ct = default)
    {
        if (DiscoveryFailure is not null)
            throw DiscoveryFailure;
        if (InstallationMissing)
            throw new GitHubAppInstallationException(
                "The GitHub App is not installed for this Repository.",
                "github_app_installation_required",
                new { installationUrl = "https://github.com/apps/mohist/installations/new" });
        return Task.FromResult(new GitHubRepositoryInstallation(
            InstallationId,
            owner.Trim().ToLowerInvariant(),
            repo.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(RepositoryNodeId) || RepositoryNodeId == "repository-node-test"
                ? $"repository-node-{Interlocked.Increment(ref _repositorySequence)}"
                : RepositoryNodeId));
    }

    public Task<GitHubInstallationToken> CreateInstallationTokenAsync(string installationId, CancellationToken ct = default) =>
        Task.FromResult(new GitHubInstallationToken("installation-token", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
}
