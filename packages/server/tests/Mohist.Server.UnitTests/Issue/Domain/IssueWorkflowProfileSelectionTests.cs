using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

/// <summary>
/// Covers the persisted workflow profile selection on the
/// <see cref="Issue"/> aggregate introduced for issue-workflow-profile
/// consistency (single source of truth).
/// </summary>
public class IssueWorkflowProfileSelectionTests
{
    private static IssueWorkflowProfileChanged UnwrapChanged(IssueEvent payload) => payload switch
    {
        IssueWorkflowProfileChanged c => c,
        _ => throw new InvalidOperationException($"Expected IssueWorkflowProfileChanged, got {payload.GetType().Name}"),
    };

    [Fact]
    public void Create_WithoutProfile_LeavesSelectionNull()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Plain");

        Assert.Null(issue.WorkflowProfileId);
    }

    [Fact]
    public void State_RoundTripsExplicitSelection()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "PR issue");
        issue.ReplaceWorkflowProfile("mohist/github-pr");

        var json = IssueStore.Serialize(issue);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("workflowProfileId", out var el));
        Assert.Equal("mohist/github-pr", el.GetString());

        var reloaded = IssueStore.Deserialize(json);

        Assert.NotNull(reloaded);
        Assert.Equal("mohist/github-pr", reloaded!.WorkflowProfileId);
    }

    [Fact]
    public void State_RoundTripsNullSelectionForLegacyIssues()
    {
        // Legacy issue rows serialized before the field was added must
        // continue to deserialize with a null selection (issue #257
        // design — additive, null-safe, no migration).
        const string legacyJson = """
            {
              "projectId": "project-1",
              "number": 1,
              "title": "Legacy",
              "labels": {},
              "priority": "p2",
              "isDraft": false,
              "createdAt": "2026-06-01T00:00:00Z",
              "updatedAt": "2026-06-01T00:00:00Z"
            }
            """;

        var reloaded = IssueStore.Deserialize(legacyJson);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.WorkflowProfileId);
    }

    [Fact]
    public void ReplaceWorkflowProfile_RecordsChangeEvent()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Title");

        issue.ReplaceWorkflowProfile("mohist/github-pr");
        issue.ClearPendingEvents();
        issue.ReplaceWorkflowProfile("mohist/local");

        var evt = Assert.Single(issue.PendingEvents);
        var changed = UnwrapChanged(evt);
        Assert.Equal("mohist/local", changed.WorkflowProfileId);
    }

    [Fact]
    public void ReplaceWorkflowProfile_NullClearsSelection()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Title");
        issue.ReplaceWorkflowProfile("mohist/github-pr");
        issue.ClearPendingEvents();

        issue.ReplaceWorkflowProfile(null);

        Assert.Null(issue.WorkflowProfileId);
        var evt = Assert.Single(issue.PendingEvents);
        var changed = UnwrapChanged(evt);
        Assert.Null(changed.WorkflowProfileId);
    }

    [Fact]
    public void ReplaceWorkflowProfile_SameValueIsNoOp()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Title");
        issue.ReplaceWorkflowProfile("mohist/github-pr");
        issue.ClearPendingEvents();

        issue.ReplaceWorkflowProfile("mohist/github-pr");

        Assert.Empty(issue.PendingEvents);
    }

    [Fact]
    public void ReplaceWorkflowProfile_NormalizesWhitespace()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project-1", 1, "Title");

        issue.ReplaceWorkflowProfile("   ");

        Assert.Null(issue.WorkflowProfileId);
    }
}
