using System.Text.Json;
using Mohist.Server.GitHub;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubEventNormalizerTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly string IssuesLabeled = """
        {
          "action": "labeled",
          "number": 42,
          "issue": { "number": 42, "title": "Fix the bug", "state": "open" },
          "repository": { "name": "hello-world", "full_name": "octocat/hello-world", "owner": { "login": "octocat" } }
        }
        """;

    private static readonly string ReviewSubmitted = """
        {
          "action": "submitted",
          "pull_request": { "number": 7, "title": "Add feature" },
          "review": { "state": "approved", "user": { "login": "alice" } },
          "repository": { "name": "hello-world", "full_name": "octocat/hello-world", "owner": { "login": "octocat" } }
        }
        """;

    private static readonly string CheckSuiteCompleted = """
        {
          "action": "completed",
          "check_suite": { "id": 5, "conclusion": "success" },
          "repository": { "name": "hello-world", "full_name": "octocat/hello-world", "owner": { "login": "octocat" } }
        }
        """;

    private static CloudEvent? Normalize(string eventHeader, string body) =>
        GitHubEventNormalizer.Normalize(
            eventHeader, Payload(body), "proj_1", "ghconn_1", "delivery-1", ReceivedAt);

    [Theory]
    [InlineData("issues", "labeled", EventCatalog.ReverseDns.GitHubIssuesLabeled)]
    [InlineData("issues", "closed", EventCatalog.ReverseDns.GitHubIssuesClosed)]
    [InlineData("issues", "reopened", EventCatalog.ReverseDns.GitHubIssuesReopened)]
    public void IssuesEvent_MapsActionToType(string header, string action, string expectedType)
    {
        var body = IssuesLabeled.Replace("\"labeled\"", $"\"{action}\"");
        var envelope = Normalize(header, body);

        Assert.NotNull(envelope);
        Assert.Equal(expectedType, envelope.Type);
        Assert.Equal("/mohist/projects/proj_1/github-connections/ghconn_1", envelope.Source.ToString());
        Assert.Equal("delivery-1", envelope.Id);
        Assert.Equal(ReceivedAt, envelope.Time);
        Assert.Equal("application/json", envelope.DataContentType);
        Assert.Equal("proj_1", envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("octocat/hello-world", envelope.Extensions[EventCatalog.Lineage.GitHubRepo]);
        Assert.Equal("42", envelope.Extensions[EventCatalog.Lineage.GitHubIssue]);
        Assert.DoesNotContain(EventCatalog.Lineage.Issue, envelope.Extensions.Keys);
    }

    [Fact]
    public void PullRequestReviewEvent_MapsToReviewedWithPrNumber()
    {
        var envelope = Normalize("pull_request_review", ReviewSubmitted);

        Assert.NotNull(envelope);
        Assert.Equal(EventCatalog.ReverseDns.GitHubPullRequestReviewed, envelope.Type);
        Assert.Equal("7", envelope.Extensions[EventCatalog.Lineage.GitHubIssue]);
    }

    [Fact]
    public void CheckSuiteCompletedEvent_MapsToCompletedWithoutIssueNumber()
    {
        var envelope = Normalize("check_suite", CheckSuiteCompleted);

        Assert.NotNull(envelope);
        Assert.Equal(EventCatalog.ReverseDns.GitHubCheckSuiteCompleted, envelope.Type);
        Assert.Equal("octocat/hello-world", envelope.Extensions[EventCatalog.Lineage.GitHubRepo]);
        Assert.DoesNotContain(EventCatalog.Lineage.GitHubIssue, envelope.Extensions.Keys);
    }

    [Theory]
    [InlineData("issues", "opened")] // not in the v1 set
    [InlineData("ping", "")]
    [InlineData("check_suite", "requested")]
    public void UnmappedEvents_ReturnNull(string header, string action)
    {
        var body = IssuesLabeled.Replace("\"labeled\"", $"\"{action}\"");
        Assert.Null(Normalize(header, body));
    }

    [Fact]
    public void MissingRepositoryCoordinate_OmitsGitHubRepoExtension()
    {
        var body = """{ "action": "labeled", "issue": { "number": 42 } }""";
        var envelope = Normalize("issues", body);

        Assert.NotNull(envelope);
        Assert.Equal(EventCatalog.ReverseDns.GitHubIssuesLabeled, envelope.Type);
        Assert.DoesNotContain(EventCatalog.Lineage.GitHubRepo, envelope.Extensions.Keys);
        Assert.Equal("42", envelope.Extensions[EventCatalog.Lineage.GitHubIssue]);
    }

    [Fact]
    public void EmptyDeliveryId_FallsBackToGeneratedId()
    {
        var envelope = GitHubEventNormalizer.Normalize(
            "issues", Payload(IssuesLabeled), "proj_1", "ghconn_1", string.Empty, ReceivedAt);

        Assert.NotNull(envelope);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Id));
        Assert.NotEqual("delivery-1", envelope.Id);
    }

    [Fact]
    public void PayloadIsPreservedVerbatim()
    {
        var envelope = Normalize("issues", IssuesLabeled);

        Assert.NotNull(envelope);
        Assert.Equal("Fix the bug", envelope.Data!.Value.GetProperty("issue").GetProperty("title").GetString());
        Assert.Equal("labeled", envelope.Data.Value.GetProperty("action").GetString());
    }
}
