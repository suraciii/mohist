using System.Text.Json;
using Mohist.Server.GitHub;
using Xunit;

namespace Mohist.Server.L0Tests.GitHub;

[Trait("level", "L0")]
public sealed class GitHubIssueCommentEventPayloadTests
{
    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Parse_ReadsCommentAndIssueSnapshot()
    {
        var payload = GitHubIssueCommentEventPayload.Parse(Payload("""
            {
              "issue": {
                "number": 42,
                "title": "Fix the bug",
                "body": "Steps",
                "labels": [ { "name": "p1" } ]
              },
              "comment": {
                "id": 1001,
                "body": "/mohist start",
                "author_association": "MEMBER"
              }
            }
            """));

        Assert.NotNull(payload);
        Assert.Equal(42, payload.IssueNumber);
        Assert.Equal("Fix the bug", payload.Title);
        Assert.Equal("Steps", payload.Body);
        Assert.Equal(["p1"], payload.Labels);
        Assert.Equal("1001", payload.CommentId);
        Assert.Equal("/mohist start", payload.CommentBody);
        Assert.Equal("MEMBER", payload.AuthorAssociation);
    }

    [Theory]
    [InlineData("/mohist start", GitHubIssueCommandVerb.Start)]
    [InlineData("/MOHIST START", GitHubIssueCommandVerb.Start)]
    [InlineData("/mohist stop", GitHubIssueCommandVerb.Unknown)]
    [InlineData("/mohist", GitHubIssueCommandVerb.Unknown)]
    public void CommandParser_MapsOnlySupportedVerb(string body, GitHubIssueCommandVerb expected)
    {
        Assert.Equal(expected, GitHubIssueCommand.Parse(body)!.Verb);
    }

    [Theory]
    [InlineData("OWNER", true)]
    [InlineData("member", true)]
    [InlineData("COLLABORATOR", true)]
    [InlineData("CONTRIBUTOR", false)]
    [InlineData("NONE", false)]
    public void CommandParser_UsesGitHubAuthorAssociation(string association, bool expected)
    {
        Assert.Equal(expected, GitHubIssueCommand.IsPermitted(association));
    }

    [Theory]
    [InlineData("please /mohist start")]
    [InlineData("/mohist-start")]
    public void CommandParser_IgnoresNonCommands(string body)
    {
        Assert.Null(GitHubIssueCommand.Parse(body));
    }

    [Fact]
    public void CommandParser_RejectsUnexpectedArguments()
    {
        Assert.Equal(GitHubIssueCommandVerb.Unknown, GitHubIssueCommand.Parse("/mohist start extra")!.Verb);
    }
}
