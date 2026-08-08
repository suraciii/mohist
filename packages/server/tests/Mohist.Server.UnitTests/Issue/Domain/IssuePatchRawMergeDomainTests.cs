using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Domain;

/// <summary>
/// Pure-domain specs for the raw presence merge rules exercised by
/// <c>PATCH /api/projects/{ref}/issues/{number}</c>. The grain calls
/// <see cref="Mohist.Server.Issue.Domain.Issue.Update"/> once per
/// request with each field either present (passed through) or absent
/// (passed as <c>null</c>). The merge rule for the labels map is the
/// odd one out: absent preserves existing labels, explicit <c>null</c>
/// clears them, present map replaces. The route-level shapes
/// (200/400/404 + JSON envelope + multipart binding + attachment
/// upload lifecycle) stay in <c>IssuePatchRawPresenceMergeSpecs</c>.
/// </summary>
public class IssuePatchRawMergeDomainTests
{
    [Fact]
    public void Update_AbsentLabels_PreservesExistingLabels()
    {
        var issue = NewIssueWithLabels(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream"] = "frontend",
            ["module"] = "auth",
        });

        // Patch body and title, but leave labels absent (the default).
        issue.Update(title: "updated", body: "new body", labels: null, priority: null);

        Assert.Equal("frontend", issue.Labels["stream"]);
        Assert.Equal("auth", issue.Labels["module"]);
    }

    [Fact]
    public void Update_NullLabels_ClearsLabelMapToEmpty()
    {
        var issue = NewIssueWithLabels(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream"] = "frontend",
            ["module"] = "auth",
        });

        // labels == empty dictionary signals "clear to empty".
        issue.Update(title: null, body: null, labels: new Dictionary<string, string>(StringComparer.Ordinal), priority: null);

        Assert.Empty(issue.Labels);
    }

    [Fact]
    public void Update_PresentLabels_ReplacesLabelMapInFull()
    {
        var issue = NewIssueWithLabels(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream"] = "frontend",
            ["old"] = "stale",
        });

        issue.Update(
            title: null,
            body: null,
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" },
            priority: null);

        Assert.Single(issue.Labels);
        Assert.Equal("v", issue.Labels["k"]);
        Assert.False(issue.Labels.ContainsKey("stream"));
        Assert.False(issue.Labels.ContainsKey("old"));
    }

    [Fact]
    public void Update_AbsentTitle_PreservesExistingTitle()
    {
        var issue = NewIssue(title: "Original title", labels: new Dictionary<string, string>(StringComparer.Ordinal));

        issue.Update(title: null, body: "new body", labels: null, priority: null);

        Assert.Equal("Original title", issue.Title);
    }

    [Fact]
    public void Update_PresentTitle_ReplacesTitle()
    {
        var issue = NewIssue(title: "Original title", labels: new Dictionary<string, string>(StringComparer.Ordinal));

        issue.Update(title: "Updated title", body: null, labels: null, priority: null);

        Assert.Equal("Updated title", issue.Title);
    }

    [Fact]
    public void Update_PresentBody_ReplacesBody()
    {
        var issue = NewIssue(title: "T", body: "Original body", labels: new Dictionary<string, string>(StringComparer.Ordinal));

        issue.Update(title: null, body: "New body", labels: null, priority: null);

        Assert.Equal("New body", issue.Body);
    }

    [Fact]
    public void Update_OnlyLabels_LeavesOtherFieldsUnchanged()
    {
        var issue = NewIssue(
            title: "Title",
            body: "Original body",
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "auth" },
            priority: "p1");

        issue.Update(
            title: null,
            body: null,
            labels: new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "auth" },
            priority: null);

        Assert.Equal("Title", issue.Title);
        Assert.Equal("Original body", issue.Body);
        Assert.Equal("p1", issue.Priority);
    }

    [Fact]
    public void Update_PresentPriority_ReplacesPriority()
    {
        var issue = NewIssue(title: "T", priority: "p2", labels: new Dictionary<string, string>(StringComparer.Ordinal));

        issue.Update(title: null, body: null, labels: null, priority: "p0");

        Assert.Equal("p0", issue.Priority);
    }

    [Fact]
    public void Update_PresentRisk_RecordsRiskWhenUpdateRiskIsTrue()
    {
        var issue = NewIssueWithLabels(new Dictionary<string, string>(StringComparer.Ordinal));

        issue.Update(title: null, body: null, labels: null, priority: null, risk: "high", updateRisk: true);

        Assert.Equal("high", issue.Risk);
    }

    [Fact]
    public void Update_AbsentRisk_LeavesRiskUnchangedWhenUpdateRiskIsFalse()
    {
        var issue = NewIssueWithLabels(new Dictionary<string, string>(StringComparer.Ordinal));
        // Seed via Update so we can drive the same field later.
        issue.Update(title: null, body: null, labels: null, priority: null, risk: "low", updateRisk: true);
        Assert.Equal("low", issue.Risk);

        issue.Update(title: null, body: null, labels: null, priority: null, risk: null, updateRisk: false);

        Assert.Equal("low", issue.Risk);
    }

    [Fact]
    public void SetDraft_TrueThenFalse_FlipsDraftState()
    {
        var issue = NewIssue(title: "T", labels: new Dictionary<string, string>(StringComparer.Ordinal), isDraft: true);
        Assert.True(issue.IsDraft);

        issue.SetDraft(false);
        Assert.False(issue.IsDraft);
    }

    [Fact]
    public void SetDraft_OnInProgressIssue_Throws()
    {
        var issue = DomainIssue.Create(
            projectId: "proj-startdraft",
            number: 2,
            title: "Started",
            repositoryRef: "main",
            isDraft: true,
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_seed");

        Assert.Throws<InvalidOperationException>(() => issue.SetDraft(false));
    }

    private static DomainIssue NewIssueWithLabels(IReadOnlyDictionary<string, string> labels)
    {
        return NewIssue(title: "Merge test", labels: labels);
    }

    private static DomainIssue NewIssue(
        string title,
        IReadOnlyDictionary<string, string> labels,
        string? body = null,
        string priority = "p2",
        bool isDraft = false)
    {
        return DomainIssue.Create(
            projectId: "proj-merge",
            number: 1,
            title: title,
            body: body,
            labels: labels,
            priority: priority,
            repositoryRef: "main",
            isDraft: isDraft,
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
