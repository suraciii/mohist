using System.Text.Json;
using Mohist.Server.GitHub;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubIssueEventPayloadTests
{
    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Parse_ReadsIssueSnapshotFields()
    {
        var payload = GitHubIssueEventPayload.Parse(Payload("""
            {
              "issue": {
                "number": 42,
                "title": "Fix the bug",
                "body": "Steps to reproduce",
                "labels": [ { "name": "mohist" }, { "name": "p1" } ]
              }
            }
            """));

        Assert.NotNull(payload);
        Assert.Equal(42, payload.IssueNumber);
        Assert.Equal("Fix the bug", payload.Title);
        Assert.Equal("Steps to reproduce", payload.Body);
        Assert.Equal(new[] { "mohist", "p1" }, payload.Labels);
    }

    [Fact]
    public void Parse_NullBody_YieldsNullBody()
    {
        var payload = GitHubIssueEventPayload.Parse(Payload("""
            { "issue": { "number": 1, "title": "No body", "labels": [] } }
            """));

        Assert.NotNull(payload);
        Assert.Null(payload.Body);
        Assert.Empty(payload.Labels);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "issue": { "title": "no number" } }""")]
    [InlineData("""{ "issue": { "number": 0, "title": "zero" } }""")]
    [InlineData("""{ "issue": { "number": 3, "title": "  " } }""")]
    [InlineData("null")]
    public void Parse_MalformedPayload_ReturnsNull(string json)
    {
        Assert.Null(GitHubIssueEventPayload.Parse(Payload(json)));
    }

    [Fact]
    public void Parse_NullData_ReturnsNull()
    {
        Assert.Null(GitHubIssueEventPayload.Parse(null));
    }

    [Theory]
    [InlineData(new[] { "mohist", "p0" }, "p0")]
    [InlineData(new[] { "p4" }, "p4")]
    [InlineData(new[] { "P2" }, "p2")]
    [InlineData(new[] { "mohist" }, null)]
    [InlineData(new[] { "priority:p1" }, null)]
    [InlineData(new[] { "p5" }, null)]
    [InlineData(new[] { "p10" }, null)]
    [InlineData(new string[] { }, null)]
    public void MapPriority_MapsPLabelsOnly(string[] labels, string? expected)
    {
        Assert.Equal(expected, GitHubIssueFeedTranslation.MapPriority(labels));
    }
}
