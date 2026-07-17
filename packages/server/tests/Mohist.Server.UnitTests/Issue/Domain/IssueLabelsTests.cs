using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

public class IssueLabelsTests
{
    private static IssueCreated UnwrapCreated(IssueEvent payload) => payload switch
    {
        IssueCreated c => c,
        _ => throw new InvalidOperationException($"Expected IssueCreated, got {payload.GetType().Name}"),
    };

    private static IssueLabelsChanged UnwrapLabelsChanged(IssueEvent payload) => payload switch
    {
        IssueLabelsChanged c => c,
        _ => throw new InvalidOperationException($"Expected IssueLabelsChanged, got {payload.GetType().Name}"),
    };

    [Fact]
    public void Create_WithLabels_RecordsLabelsInCreatedEvent()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream"] = "frontend",
            ["module"] = "auth",
        };

        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: labels,
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));

        var evt = issue.PendingEvents.Single();
        var created = UnwrapCreated(evt);
        Assert.Equal("frontend", created.Labels["stream"]);
        Assert.Equal("auth", created.Labels["module"]);
    }

    [Fact]
    public void SetLabel_AddsNewKey_AndEmitsLabelsChanged()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");
        issue.ClearPendingEvents();

        issue.SetLabel("stream", "frontend", new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Equal("frontend", issue.Labels["stream"]);
        var evt = issue.PendingEvents.Single();
        Assert.True(evt is IssueLabelsChanged);
        var labelsChanged = UnwrapLabelsChanged(evt);
        Assert.Empty(labelsChanged.OldLabels);
        Assert.Equal("frontend", labelsChanged.NewLabels["stream"]);
    }

    [Fact]
    public void SetLabel_OnExistingKey_ReplacesValue_AndEmitsEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });
        issue.ClearPendingEvents();

        issue.SetLabel("stream", "backend", new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Equal("backend", issue.Labels["stream"]);
        var evt = issue.PendingEvents.Single();
        Assert.True(evt is IssueLabelsChanged);
        var labelsChanged = UnwrapLabelsChanged(evt);
        Assert.Equal("frontend", labelsChanged.OldLabels["stream"]);
        Assert.Equal("backend", labelsChanged.NewLabels["stream"]);
    }

    [Fact]
    public void SetLabel_NoOpChange_DoesNotEmitEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc),
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });
        var updatedAt = issue.UpdatedAt;
        issue.ClearPendingEvents();

        issue.SetLabel("stream", "frontend", new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Empty(issue.PendingEvents);
        Assert.Equal(updatedAt, issue.UpdatedAt);
    }

    [Fact]
    public void RemoveLabel_ExistingKey_EmitsEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });
        issue.ClearPendingEvents();

        issue.RemoveLabel("stream", new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.False(issue.Labels.ContainsKey("stream"));
        var evt = issue.PendingEvents.Single();
        Assert.True(evt is IssueLabelsChanged);
        var labelsChanged = UnwrapLabelsChanged(evt);
        Assert.Equal("frontend", labelsChanged.OldLabels["stream"]);
        Assert.Empty(labelsChanged.NewLabels);
    }

    [Fact]
    public void RemoveLabel_MissingKey_IsIdempotentAndEmitsNoEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });
        issue.ClearPendingEvents();

        issue.RemoveLabel("missing");

        Assert.Empty(issue.PendingEvents);
    }

    [Fact]
    public void ReplaceLabels_FullReplace_EmitsEventWithBeforeAndAfter()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["old"] = "x",
            });
        issue.ClearPendingEvents();

        issue.ReplaceLabels(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "auth" },
            new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Equal("auth", issue.Labels["module"]);
        Assert.False(issue.Labels.ContainsKey("stream"));
        Assert.False(issue.Labels.ContainsKey("old"));

        var evt = issue.PendingEvents.Single();
        Assert.True(evt is IssueLabelsChanged);
        var labelsChanged = UnwrapLabelsChanged(evt);
        Assert.Equal("frontend", labelsChanged.OldLabels["stream"]);
        Assert.Equal("x", labelsChanged.OldLabels["old"]);
        Assert.Equal("auth", labelsChanged.NewLabels["module"]);
    }

    [Fact]
    public void SetLabel_InvalidKey_ThrowsArgumentException()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");

        Assert.Throws<ArgumentException>(() => issue.SetLabel("Stream", "frontend"));
        Assert.Throws<ArgumentException>(() => issue.SetLabel("stream frontend", "frontend"));
        Assert.Throws<ArgumentException>(() => issue.SetLabel("-stream", "frontend"));
        Assert.Throws<ArgumentException>(() => issue.SetLabel("stream-", "frontend"));
        Assert.Throws<ArgumentException>(() => issue.SetLabel("", "frontend"));
    }

    [Fact]
    public void SetLabel_EmptyValue_ThrowsArgumentException()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");

        Assert.Throws<ArgumentException>(() => issue.SetLabel("stream", ""));
        Assert.Throws<ArgumentException>(() => issue.SetLabel("stream", "   "));
    }

    [Fact]
    public void SetLabel_ValidKey_Accepts()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Build the feature");

        issue.SetLabel("stream", "frontend");
        issue.SetLabel("module-auth", "core");
        issue.SetLabel("stream--auth", "double-dash");
        issue.SetLabel("a1", "v");

        Assert.Equal("frontend", issue.Labels["stream"]);
        Assert.Equal("core", issue.Labels["module-auth"]);
        Assert.Equal("double-dash", issue.Labels["stream--auth"]);
        Assert.Equal("v", issue.Labels["a1"]);
    }

    [Fact]
    public void Create_WithInvalidLabelKey_ThrowsArgumentException()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["Stream"] = "frontend" };

        Assert.Throws<ArgumentException>(() =>
            Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Bad", labels: labels));
    }

    [Fact]
    public void Create_WithEmptyLabelValue_ThrowsArgumentException()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "  " };

        Assert.Throws<ArgumentException>(() =>
            Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Bad", labels: labels));
    }

    [Fact]
    public void Update_LabelsFullReplacement_RecordsChange()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });
        issue.ClearPendingEvents();

        issue.Update(
            title: null,
            body: null,
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "auth" },
            priority: null,
            now: new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Equal("auth", issue.Labels["module"]);
        Assert.False(issue.Labels.ContainsKey("stream"));

        var evt = UnwrapLabelsChanged(issue.PendingEvents.Single());
        Assert.Equal("frontend", evt.OldLabels["stream"]);
        Assert.Equal("auth", evt.NewLabels["module"]);
    }

    [Fact]
    public void Update_NoOpLabelChange_DoesNotEmitEvent()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" };
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: labels,
            now: new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc));
        var updatedAt = issue.UpdatedAt;
        issue.ClearPendingEvents();

        issue.Update(
            title: null,
            body: null,
            labels: labels,
            priority: null,
            now: new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc));

        Assert.Empty(issue.PendingEvents);
        Assert.Equal(updatedAt, issue.UpdatedAt);
    }

    [Fact]
    public void Deserialize_LegacyArrayLabels_NoLongerNormalized()
    {
        var legacyJson = """
        {
          "projectId": "project-1",
          "number": 1,
          "title": "Legacy issue",
          "body": null,
          "labels": ["bug", "urgent"],
          "priority": "p2",
          "risk": null,
          "repositoryRef": null,
          "createdAt": "2026-06-05T01:00:00Z",
          "updatedAt": "2026-06-05T01:00:00Z",
          "archivedAt": null,
          "status": "Backlog",
          "prerequisiteNumbers": []
        }
        """;

        Assert.ThrowsAny<Exception>(() => IssueStore.Deserialize(legacyJson));
    }

    [Fact]
    public void Deserialize_ObjectLabels_RoundTripsAsMap()
    {
        var json = """
        {
          "projectId": "project-1",
          "number": 1,
          "title": "Object labels",
          "body": null,
          "labels": { "stream": "frontend", "module": "auth" },
          "priority": "p2",
          "risk": null,
          "repositoryRef": null,
          "createdAt": "2026-06-05T01:00:00Z",
          "updatedAt": "2026-06-05T01:00:00Z",
          "archivedAt": null,
          "status": "Backlog",
          "prerequisiteNumbers": []
        }
        """;

        var issue = IssueStore.Deserialize(json);

        Assert.NotNull(issue);
        Assert.Equal("frontend", issue!.Labels["stream"]);
        Assert.Equal("auth", issue.Labels["module"]);
    }

    [Fact]
    public void Serialize_Labels_WritesObject()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project-1",
            1,
            "Build the feature",
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });

        var json = IssueStore.Serialize(issue);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("labels").ValueKind);
        Assert.Equal("frontend", doc.RootElement.GetProperty("labels").GetProperty("stream").GetString());
    }
}
