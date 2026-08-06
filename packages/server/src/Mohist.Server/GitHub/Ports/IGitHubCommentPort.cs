using Mohist.Server.GitHub.Domain;

namespace Mohist.Server.GitHub.Ports;

/// <summary>
/// Write-back channel to GitHub issues: posts comments, swaps the
/// <c>mohist:</c> state label, and closes issues as the connection's GitHub
/// identity. Failures propagate to the caller (best-effort semantics live
/// in the caller, not here).
/// </summary>
public interface IGitHubCommentPort
{
    Task PostCommentAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string body,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces the mutually-exclusive <c>mohist:</c> state label on the
    /// issue with <paramref name="stateLabel"/>: existing <c>mohist:</c>
    /// labels are removed, all other labels are kept.
    /// </summary>
    Task ReplaceStateLabelAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string stateLabel,
        CancellationToken ct = default);

    /// <summary>
    /// Closes the issue with the given GitHub <c>state_reason</c>
    /// (<c>completed</c> or <c>not_planned</c>). Closing an already-closed
    /// issue is a no-op on the GitHub side.
    /// </summary>
    Task CloseIssueAsync(
        GitHubConnection connection,
        int githubIssueNumber,
        string stateReason,
        CancellationToken ct = default);
}
