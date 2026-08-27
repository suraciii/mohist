using Mohist.Server.GitHub.Domain;

namespace Mohist.Server.GitHub.Ports;

public sealed record GitHubIssueSnapshot(
    int Number,
    string Title,
    string? Body,
    string? State = null,
    string? StateReason = null);

// The content mirror uses the same GitHub REST adapter as progress write-back.
// This alias keeps the content seam explicit without introducing another port.
public interface IGitHubIssuePort
{
    Task<int> CreateIssueAsync(GitHubConnection connection, string title, string body, string marker, CancellationToken ct = default);
    Task<int?> FindIssueByMarkerAsync(GitHubConnection connection, string marker, CancellationToken ct = default);
    Task<GitHubIssueSnapshot?> GetIssueAsync(GitHubConnection connection, int githubIssueNumber, CancellationToken ct = default);
    Task UpdateIssueAsync(GitHubConnection connection, int githubIssueNumber, string title, string body, string marker, CancellationToken ct = default);
}
