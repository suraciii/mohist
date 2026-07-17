using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public sealed class CloudEventLineageTests
{
    [Fact]
    public void TryReadIssueContext_ReadsCanonicalProjectIssueAndEpic()
    {
        var evt = Event(
            new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
                [EventCatalog.Lineage.Issue] = "42",
                [EventCatalog.Lineage.Epic] = "7",
            });

        Assert.True(CloudEventLineage.TryReadIssueContext(evt, out var context));
        Assert.Equal("proj_a", context.ProjectId);
        Assert.Equal(42, context.IssueNumber);
        Assert.Equal(7, context.EpicNumber);
    }

    [Fact]
    public void TryReadIssueContext_IgnoresInvalidOptionalEpic()
    {
        var evt = Event(
            new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = "proj_a",
                [EventCatalog.Lineage.Issue] = "42",
                [EventCatalog.Lineage.Epic] = "0",
            });

        Assert.True(CloudEventLineage.TryReadIssueContext(evt, out var context));
        Assert.Null(context.EpicNumber);
    }

    [Fact]
    public void TryReadEpicContext_ReadsCanonicalProjectAndEpic()
    {
        var extensions = new Dictionary<string, string>
        {
            [EventCatalog.Lineage.ProjectId] = "proj_a",
            [EventCatalog.Lineage.Epic] = "7",
        };

        Assert.True(CloudEventLineage.TryReadEpicContext(extensions, out var context));
        Assert.Equal("proj_a", context.ProjectId);
        Assert.Equal(7, context.EpicNumber);
    }

    [Fact]
    public void TryReadEpicContext_RejectsNonPositiveEpic()
    {
        var extensions = new Dictionary<string, string>
        {
            [EventCatalog.Lineage.ProjectId] = "proj_a",
            [EventCatalog.Lineage.Epic] = "0",
        };

        Assert.False(CloudEventLineage.TryReadEpicContext(extensions, out _));
    }

    [Theory]
    [InlineData("", "42")]
    [InlineData("proj_a", "0")]
    [InlineData("proj_a", "not-a-number")]
    public void TryReadIssueContext_RequiresCanonicalProjectAndPositiveIssue(
        string projectId,
        string issue)
    {
        var evt = Event(
            new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = projectId,
                [EventCatalog.Lineage.Issue] = issue,
            });

        Assert.False(CloudEventLineage.TryReadIssueContext(evt, out _));
    }

    [Fact]
    public void ReadValue_DoesNotInspectPayload()
    {
        var evt = Event(
            new Dictionary<string, string>(),
            JsonSerializer.SerializeToElement(new { workflowRunId = "payload-run" }));

        Assert.Null(CloudEventLineage.ReadValue(evt.Extensions, EventCatalog.Lineage.WorkflowRunId));
    }

    private static CloudEvent Event(
        IReadOnlyDictionary<string, string> extensions,
        JsonElement? data = null) =>
        new(
            id: "evt",
            source: new Uri("/mohist/source", UriKind.Relative),
            type: "test.event",
            time: DateTimeOffset.UnixEpoch,
            data: data,
            extensions: extensions);
}
