using System.Text.Json;

namespace Mohist.Server.GitHub;

/// <summary>
/// The slice of a GitHub <c>pull_request_review</c> webhook payload the
/// approval translator consumes. Only <c>submitted</c> deliveries parse
/// (edited / dismissed carry no decision); the review state, reviewer
/// login, review body, and the PR head branch are the only fields read.
/// </summary>
public sealed record GitHubPullRequestReviewEventPayload(
    int PullRequestNumber,
    string HeadBranch,
    string ReviewerLogin,
    string State,
    string? Body)
{
    public static GitHubPullRequestReviewEventPayload? Parse(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            return null;
        if (!element.TryGetProperty("action", out var action) || action.GetString() != "submitted")
            return null;
        if (!element.TryGetProperty("review", out var review) || review.ValueKind != JsonValueKind.Object)
            return null;
        if (!review.TryGetProperty("user", out var user)
            || user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("login", out var login)
            || string.IsNullOrWhiteSpace(login.GetString()))
        {
            return null;
        }
        if (!review.TryGetProperty("state", out var state)
            || state.GetString() is not (GitHubPullRequestReviewState.Approved
                or GitHubPullRequestReviewState.ChangesRequested
                or GitHubPullRequestReviewState.Commented))
        {
            return null;
        }
        if (!element.TryGetProperty("pull_request", out var pullRequest) || pullRequest.ValueKind != JsonValueKind.Object)
            return null;
        if (!pullRequest.TryGetProperty("head", out var head) || head.ValueKind != JsonValueKind.Object)
            return null;
        if (!head.TryGetProperty("ref", out var refValue) || string.IsNullOrWhiteSpace(refValue.GetString()))
            return null;
        var number = pullRequest.TryGetProperty("number", out var numberValue) && numberValue.TryGetInt32(out var prNumber)
            ? prNumber
            : 0;
        var body = review.TryGetProperty("body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.String
            ? bodyValue.GetString()
            : null;
        return new GitHubPullRequestReviewEventPayload(
            number,
            refValue.GetString()!,
            login.GetString()!.Trim(),
            state.GetString()!,
            body);
    }
}

public static class GitHubPullRequestReviewState
{
    public const string Approved = "approved";
    public const string ChangesRequested = "changes_requested";
    public const string Commented = "commented";
}

/// <summary>
/// Pure translation rules from a submitted PR review to the Check gate
/// decision, per the design's approval translator.
/// </summary>
public static class GitHubPullRequestReviewTranslation
{
    public const string BranchIssuePrefix = "mo/issue-";

    public static bool TryParseIssueNumber(string branch, out int issueNumber)
    {
        issueNumber = 0;
        if (string.IsNullOrWhiteSpace(branch))
            return false;
        var name = branch;
        const string refsPrefix = "refs/heads/";
        if (name.StartsWith(refsPrefix, StringComparison.Ordinal))
            name = name[refsPrefix.Length..];
        if (!name.StartsWith(BranchIssuePrefix, StringComparison.Ordinal))
            return false;
        var suffix = name[BranchIssuePrefix.Length..];
        return suffix.Length > 0
            && suffix.All(char.IsAsciiDigit)
            && int.TryParse(suffix, out issueNumber)
            && issueNumber > 0;
    }

    public static string DecidedBy(string login) => $"github:{login}";

    public static string ChangeRequestReason(string? body) =>
        string.IsNullOrWhiteSpace(body) ? "Changes requested via GitHub review" : body.Trim();
}
