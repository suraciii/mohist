using System.Text.Json;

namespace Mohist.Server.GitHub;

/// <summary>
/// The slice of a GitHub <c>pull_request_review</c> webhook payload the
/// approval translator consumes. Only <c>submitted</c> deliveries parse
/// (edited / dismissed carry no decision); the review state, reviewer
/// login, review body, and Pull Request number are the only fields read.
/// </summary>
public sealed record GitHubPullRequestReviewEventPayload(
    int PullRequestNumber,
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
        if (!pullRequest.TryGetProperty("number", out var numberValue)
            || !numberValue.TryGetInt32(out var number)
            || number <= 0)
            return null;
        var body = review.TryGetProperty("body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.String
            ? bodyValue.GetString()
            : null;
        return new GitHubPullRequestReviewEventPayload(
            number,
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
    public static string DecidedBy(string login) => $"github:{login}";

    public static string ChangeRequestReason(string? body) =>
        string.IsNullOrWhiteSpace(body) ? "Changes requested via GitHub review" : body.Trim();
}
