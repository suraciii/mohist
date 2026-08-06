using Mohist.Server.GitHub.Domain;

namespace Mohist.Server.GitHub.Ports;

/// <summary>
/// Minimal write-back channel to GitHub issues: posts one comment on the
/// given issue as the connection's GitHub identity. Today it serves the
/// feed-rejection explanation comment only; the full write-back writer
/// builds on this port. Failures propagate to the caller (best-effort
/// semantics live in the caller, not here).
/// </summary>
public interface IGitHubCommentPort
{
    Task PostCommentAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string body,
        CancellationToken ct = default);
}
