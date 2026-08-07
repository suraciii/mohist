using Mohist.Server.Issue.Domain;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

/// <summary>
/// Pure-domain specs for the risk field persistence rules in
/// <see cref="DomainIssue"/>. The grain's <c>UpdateFullAsync</c> only
/// forwards <c>risk</c> to <see cref="DomainIssue.Update"/> when the
/// field is present; absent risk leaves the stored value untouched.
/// The HTTP-level update+round-trip shape lives in
/// <c>IssueApiSpecs.UpdateIssue_RiskOnly_PersistsAndReturnsRisk</c>.
/// </summary>
public class IssueRiskPersistingDomainSpecs
{
    [Fact]
    public void Create_WithoutRisk_LeavesRiskUnset()
    {
        var issue = NewIssue();

        Assert.Null(issue.Risk);
    }

    [Fact]
    public void Create_WithRisk_PersistsRiskOnAggregate()
    {
        var issue = DomainIssue.Create(
            projectId: "proj-risk",
            number: 1,
            title: "Risk seed",
            repositoryRef: "main",
            isDraft: false,
            risk: "high",
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("high", issue.Risk);
    }

    [Fact]
    public void Create_WithInvalidRisk_Throws()
    {
        Assert.Throws<ArgumentException>(() => DomainIssue.Create(
            projectId: "proj-risk",
            number: 1,
            title: "Bad risk",
            repositoryRef: "main",
            isDraft: false,
            risk: "catastrophic",
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Update_RiskOnly_PersistsNewRiskAndLeavesOtherFields()
    {
        var issue = NewIssue();

        issue.Update(title: null, body: null, labels: null, priority: null, risk: "high", updateRisk: true);

        Assert.Equal("high", issue.Risk);
    }

    [Fact]
    public void Update_PresentRisk_ToSameValue_LeavesRiskUnchanged()
    {
        var issue = DomainIssue.Create(
            projectId: "proj-risk",
            number: 1,
            title: "Risk noop",
            repositoryRef: "main",
            isDraft: false,
            risk: "low",
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var before = issue.UpdatedAt;
        issue.Update(title: null, body: null, labels: null, priority: null, risk: "low", updateRisk: true);

        Assert.Equal("low", issue.Risk);
        Assert.Equal(before, issue.UpdatedAt);
    }

    [Fact]
    public void Update_AbsentRisk_DoesNotTouchStoredRisk()
    {
        var issue = DomainIssue.Create(
            projectId: "proj-risk",
            number: 1,
            title: "Risk keep",
            repositoryRef: "main",
            isDraft: false,
            risk: "medium",
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        issue.Update(title: null, body: null, labels: null, priority: null, risk: null, updateRisk: false);

        Assert.Equal("medium", issue.Risk);
    }

    [Fact]
    public void Update_PresentRisk_WithInvalidValue_Throws()
    {
        var issue = NewIssue();

        Assert.Throws<ArgumentException>(() => issue.Update(
            title: null, body: null, labels: null, priority: null, risk: "extreme", updateRisk: true));
    }

    [Fact]
    public void IssueRisk_From_NormalizesCaseToLower()
    {
        var risk = IssueRisk.From("HIGH");

        Assert.NotNull(risk);
        Assert.Equal("high", risk!.Value.Value);
    }

    [Fact]
    public void IssueRisk_From_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(IssueRisk.From(null));
        Assert.Null(IssueRisk.From(""));
        Assert.Null(IssueRisk.From("   "));
    }

    [Fact]
    public void IssueRisk_From_UnknownValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => IssueRisk.From("extreme"));
    }

    private static DomainIssue NewIssue()
    {
        return DomainIssue.Create(
            projectId: "proj-risk",
            number: 1,
            title: "Risk seed",
            repositoryRef: "main",
            isDraft: false,
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}