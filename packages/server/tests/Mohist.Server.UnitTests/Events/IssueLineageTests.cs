using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// issue-419 T-001: stamps <c>parent</c> on every issue CloudEvent when
/// the producing issue carries a parent reference, mirroring how
/// <c>epic</c> is stamped. Absent affiliation omits the key entirely;
/// existing keys (projectid, issue, epic) are unchanged.
/// </summary>
public class IssueLineageTests
{
    [Fact]
    public void BuildExtensions_StampsParent_WhenParentIssueNumberSet()
    {
        var state = NewChild(parentNumber: 42);

        var extensions = IssueLineage.BuildExtensions(state);

        Assert.Equal("42", extensions[EventCatalog.Lineage.Parent]);
    }

    [Fact]
    public void BuildExtensions_OmitsParent_WhenNoParentAssignment()
    {
        var state = NewChild(parentNumber: null);

        var extensions = IssueLineage.BuildExtensions(state);

        Assert.False(extensions.ContainsKey(EventCatalog.Lineage.Parent));
    }

    [Fact]
    public void BuildExtensions_StampsParentAndEpic_AreIndependentKeys()
    {
        // Epic-only issue (cannot also be a child, by single-affiliation rule).
        var epicOnly = NewChild(parentNumber: null, epicNumber: 7);

        var extensions = IssueLineage.BuildExtensions(epicOnly);

        Assert.False(extensions.ContainsKey(EventCatalog.Lineage.Parent));
        Assert.Equal("7", extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public void BuildExtensions_StampsCanonicalKeysUnchanged()
    {
        var state = NewChild(parentNumber: 42);

        var extensions = IssueLineage.BuildExtensions(state);

        Assert.Equal("proj_lineage", extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("5", extensions[EventCatalog.Lineage.Issue]);
    }

    [Fact]
    public void BuildExtensions_NonParentIssue_DoesNotCarryParentKey()
    {
        var state = NewChild(parentNumber: null);

        var extensions = IssueLineage.BuildExtensions(state);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                EventCatalog.Lineage.ProjectId,
                EventCatalog.Lineage.Issue,
            },
            new HashSet<string>(extensions.Keys, StringComparer.Ordinal));
    }

    private static Mohist.Server.Issue.Domain.Issue NewChild(int? parentNumber, int? epicNumber = null)
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "proj_lineage",
            5,
            "any title",
            isDraft: false,
            repositoryRef: "main");
        if (parentNumber is not null)
        {
            issue.AssignParent(parentNumber.Value);
        }
        if (epicNumber is not null)
        {
            issue.AssignEpic(epicNumber.Value);
        }
        return issue;
    }
}