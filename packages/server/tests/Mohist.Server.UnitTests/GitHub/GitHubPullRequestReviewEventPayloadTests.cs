using System.Text.Json;
using Mohist.Server.GitHub;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubPullRequestReviewEventPayloadTests
{
    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string SubmittedReview = """
        {
          "action": "submitted",
          "review": {
            "state": "approved",
            "body": "Looks good",
            "user": { "login": "alice" }
          },
          "pull_request": {
            "number": 7,
            "head": { "ref": "mo/issue-42" }
          },
          "repository": { "name": "hello-world", "full_name": "octocat/hello-world", "owner": { "login": "octocat" } }
        }
        """;

    [Fact]
    public void SubmittedApprovedReview_ParsesDecisionFields()
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(Payload(SubmittedReview));

        Assert.NotNull(payload);
        Assert.Equal(7, payload!.PullRequestNumber);
        Assert.Equal("mo/issue-42", payload.HeadBranch);
        Assert.Equal("alice", payload.ReviewerLogin);
        Assert.Equal(GitHubPullRequestReviewState.Approved, payload.State);
        Assert.Equal("Looks good", payload.Body);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("dismissed")]
    public void EditedOrDismissedReview_DoesNotParse(string action)
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(
            Payload(SubmittedReview.Replace("\"submitted\"", $"\"{action}\"")));

        Assert.Null(payload);
    }

    [Theory]
    [InlineData("changes_requested")]
    [InlineData("commented")]
    public void OtherStates_ParseState(string state)
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(
            Payload(SubmittedReview.Replace("\"approved\"", $"\"{state}\"")));

        Assert.NotNull(payload);
        Assert.Equal(state, payload!.State);
    }

    [Fact]
    public void UnknownReviewState_DoesNotParse()
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(
            Payload(SubmittedReview.Replace("\"approved\"", "\"requested\"")));

        Assert.Null(payload);
    }

    [Fact]
    public void MissingHeadRef_DoesNotParse()
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(
            Payload(SubmittedReview.Replace("\"ref\": \"mo/issue-42\"", "\"ref\": \" \"")));

        Assert.Null(payload);
    }

    [Fact]
    public void MissingReviewerLogin_DoesNotParse()
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(
            Payload(SubmittedReview.Replace("\"login\": \"alice\"", "\"login\": \" \"")));

        Assert.Null(payload);
    }

    [Fact]
    public void MissingBody_IsNullBody()
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(
            Payload(SubmittedReview.Replace("\"body\": \"Looks good\",", string.Empty)));

        Assert.NotNull(payload);
        Assert.Null(payload!.Body);
    }

    [Theory]
    [InlineData("mo/issue-42", 42)]
    [InlineData("refs/heads/mo/issue-7", 7)]
    public void Branch_ResolvesIssueNumber(string branch, int expected)
    {
        Assert.True(GitHubPullRequestReviewTranslation.TryParseIssueNumber(branch, out var issueNumber));
        Assert.Equal(expected, issueNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mo/issue-")]
    [InlineData("mo/issue-0")]
    [InlineData("mo/issue-42-fix")]
    [InlineData("feature/mo/issue-42")]
    [InlineData("feature/foo")]
    [InlineData("MO/issue-42")]
    public void Branch_Unresolvable_Fails(string branch)
    {
        Assert.False(GitHubPullRequestReviewTranslation.TryParseIssueNumber(branch, out _));
    }

    [Fact]
    public void DecidedBy_PrefixesGitHubLogin()
    {
        Assert.Equal("github:alice", GitHubPullRequestReviewTranslation.DecidedBy("alice"));
    }

    [Fact]
    public void ChangeRequestReason_UsesReviewBody_OrFallback()
    {
        Assert.Equal("Please fix", GitHubPullRequestReviewTranslation.ChangeRequestReason("  Please fix  "));
        Assert.Equal(
            "Changes requested via GitHub review",
            GitHubPullRequestReviewTranslation.ChangeRequestReason(null));
        Assert.Equal(
            "Changes requested via GitHub review",
            GitHubPullRequestReviewTranslation.ChangeRequestReason("   "));
    }
}
