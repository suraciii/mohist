using Mohist.Server.GitHub.Domain;

namespace Mohist.Server.GitHub.Ports;

// The content mirror uses the same GitHub REST adapter as progress write-back.
// This alias keeps the content seam explicit without introducing another port.
public interface IGitHubIssuePort
{
    Task<int> CreateIssueAsync(GitHubConnection connection, string title, string body, string marker, CancellationToken ct = default);
    Task<int?> FindIssueByMarkerAsync(GitHubConnection connection, string marker, CancellationToken ct = default);
    Task UpdateIssueAsync(GitHubConnection connection, int githubIssueNumber, string title, string body, string marker, CancellationToken ct = default);
}
