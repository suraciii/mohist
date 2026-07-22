using Mohist.Server.Infrastructure;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

public sealed class IssueListItemSerializationTests
{
    [Fact]
    public void PreservesReadModelJsonNamesAndNeverOmitsWorkflowProfileId()
    {
        var item = new IssueListItem
        {
            Number = 473,
            Title = "Low bandwidth",
            Health = "blocked",
            StageApproval = new StageApproval
            {
                Stage = "check",
                Status = "pending",
                RequestedAt = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc),
            },
            WorkflowStageProgress = new WorkflowStageProgress("check", 2, 1, 1, 0),
            WorkflowProfileId = null,
        };

        var json = JSON.Serialize(item);

        Assert.Contains("\"health\":\"blocked\"", json);
        Assert.Contains("\"approvalState\"", json);
        Assert.Contains("\"workflowStageProgress\"", json);
        Assert.Contains("\"workflowProfileId\":null", json);
        Assert.DoesNotContain("\"stageApproval\"", json);
    }
}
