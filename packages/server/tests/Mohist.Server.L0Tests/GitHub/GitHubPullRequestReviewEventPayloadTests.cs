using System.Text.Json;
using Mohist.Server.GitHub;
using Xunit;

namespace Mohist.Server.L0Tests.GitHub;

[Trait("level", "L0")]
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
    public void MissingHead_DoesNotAffectParsing()
    {
        var payload = GitHubPullRequestReviewEventPayload.Parse(
            Payload(SubmittedReview.Replace(",\n            \"head\": { \"ref\": \"mo/issue-42\" }", string.Empty)));

        Assert.NotNull(payload);
        Assert.Equal(7, payload!.PullRequestNumber);
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
