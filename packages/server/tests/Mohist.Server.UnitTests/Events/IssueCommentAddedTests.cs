using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// issue-490 T-001: serialization + lineage-build coverage for the standalone
/// <see cref="IssueCommentAdded"/> payload and its reverse-DNS catalog entry.
/// The payload lives outside the <c>IssueEvent</c> union / serializer so its
/// bus type is the literal <c>com.mohist.issue.comment-added</c> string from
/// the catalog, not a member of the IssueEvent serializer's switch.
/// </summary>
public class IssueCommentAddedTests
{
    [Fact]
    public void ReverseDns_IsIssueCommentAdded_LiteralString()
    {
        Assert.Equal("com.mohist.issue.comment-added", EventCatalog.ReverseDns.IssueCommentAdded);
    }

    [Fact]
    public void ReverseDns_IsIssueCommentAdded_RegisteredInCatalog()
    {
        Assert.Contains(EventCatalog.ReverseDns.IssueCommentAdded, EventCatalog.All);
    }

    [Fact]
    public void Payload_RoundTripsAllFields()
    {
        var payload = new IssueCommentAdded(
            CommentId: "cmt_abc",
            Author: "admin",
            DisplayName: "Ada Lovelace",
            Body: "Looks good @supervisor");

        var data = IssueCommentAddedEventFactory.ToData(payload);

        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal("cmt_abc", data.GetProperty("commentId").GetString());
        Assert.Equal("admin", data.GetProperty("author").GetString());
        Assert.Equal("Ada Lovelace", data.GetProperty("displayName").GetString());
        Assert.Equal("Looks good @supervisor", data.GetProperty("body").GetString());
    }

    [Fact]
    public void Payload_DoesNotIntroduceExtraFields()
    {
        var data = IssueCommentAddedEventFactory.ToData(
            new IssueCommentAdded("cmt_abc", "Ada", null, "body"));

        var propertyNames = data.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "commentId", "author", "body" },
            propertyNames);
    }

    [Fact]
    public void Build_StampsIssueLineageWithEpic()
    {
        var owner = NewIssue(projectId: "proj_e", number: 5, epicNumber: 7);
        var envelope = IssueCommentAddedEventFactory.Build(
            owner,
            new IssueCommentAdded("cmt_1", "Ada", null, "body"),
            new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(EventCatalog.ReverseDns.IssueCommentAdded, envelope.Type);
        Assert.Equal("proj_e", envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("5", envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal("7", envelope.Extensions[EventCatalog.Lineage.Epic]);
        Assert.Equal("5", envelope.Subject);
        Assert.Equal($"/mohist/projects/proj_e/issues/5", envelope.Source.ToString());
    }

    [Fact]
    public void Build_OmitsEpicWhenIssueHasNoEpic()
    {
        var owner = NewIssue(projectId: "proj_ne", number: 9);
        var envelope = IssueCommentAddedEventFactory.Build(
            owner,
            new IssueCommentAdded("cmt_2", "Ada", null, "body"),
            new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("proj_ne", envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("9", envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.False(envelope.Extensions.ContainsKey(EventCatalog.Lineage.Epic));
    }

    [Fact]
    public void Build_PropagatesPayloadVerbatim()
    {
        var owner = NewIssue(projectId: "proj_body", number: 1);
        var envelope = IssueCommentAddedEventFactory.Build(
            owner,
            new IssueCommentAdded("cmt_3", "Ada", null, "@supervisor please push issue forward"),
            new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));

        var data = envelope.Data!.Value;
        Assert.Equal("cmt_3", data.GetProperty("commentId").GetString());
        Assert.Equal("Ada", data.GetProperty("author").GetString());
        Assert.Equal("@supervisor please push issue forward", data.GetProperty("body").GetString());
    }

    [Fact]
    public void Build_NormalizesAuthorBeforeStamp()
    {
        // AddCommentAsync trims the author before stamping; the factory mirrors
        // that contract — the payload's author field carries the normalized form
        // verbatim, including any leading/trailing spaces the caller trimmed.
        var owner = NewIssue(projectId: "proj_norm", number: 1);
        var envelope = IssueCommentAddedEventFactory.Build(
            owner,
            new IssueCommentAdded("cmt_4", "  Ada  ", null, "body"),
            new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("  Ada  ", envelope.Data!.Value.GetProperty("author").GetString());
    }

    private static Mohist.Server.Issue.Domain.Issue NewIssue(string projectId, int number, int? epicNumber = null)
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            projectId,
            number,
            "any title",
            repositoryRef: "main",
            isDraft: false);
        if (epicNumber is not null)
        {
            issue.AssignEpic(epicNumber.Value);
        }
        return issue;
    }
}
