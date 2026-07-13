using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

public class IssueMetricsQuerierTests
{
    [Fact]
    public void WorkCompletedConstant_MatchesIssueEventSerializerBusType()
    {
        Assert.Equal(
            IssueMetricsQuerier.WorkCompletedType,
            IssueEventSerializer.BusType(new IssueCompleted(WorkflowRunId: "wr_guard")));
    }

    [Fact]
    public void ClosedConstant_MatchesIssueEventSerializerBusType()
    {
        Assert.Equal(
            IssueMetricsQuerier.ClosedType,
            IssueEventSerializer.BusType(new IssueCancelled(Reason: null)));
    }

    [Fact]
    public void StartOfIsoWeek_ReturnsMondayForAnyInput()
    {
        Assert.Equal(
            new DateTime(2026, 6, 15),
            IssueMetricsQuerier.ISOWeekHelper.StartOfIsoWeek(new DateTime(2026, 6, 19)));
        Assert.Equal(
            new DateTime(2026, 6, 15),
            IssueMetricsQuerier.ISOWeekHelper.StartOfIsoWeek(new DateTime(2026, 6, 15)));
        Assert.Equal(
            new DateTime(2026, 6, 15),
            IssueMetricsQuerier.ISOWeekHelper.StartOfIsoWeek(new DateTime(2026, 6, 21)));
    }
}
