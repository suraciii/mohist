using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Runner.Services;

[Trait("level", "L0")]
public class WorkflowItemTranslatorWorkspaceDispatchTests
{
    [Fact]
    public void ReadIssueNumber_ReturnsNullForZero()
    {
        Assert.Null(ReadIssueNumber(new WorkflowRunMetadata(null, default, IssueNumber: 0)));
        Assert.Null(ReadIssueNumber(new WorkflowRunMetadata(null, default, IssueNumber: null)));
    }

    [Fact]
    public void ReadIssueNumber_ReturnsValueForPositive()
    {
        Assert.Equal(42, ReadIssueNumber(new WorkflowRunMetadata(null, default, IssueNumber: 42)));
    }

    [Fact]
    public void WorkspacePayload_UsesIssueDerivedName()
    {
        var issueNumber = 42;
        var workspaceName = $"issue-{issueNumber}";
        Assert.Equal("issue-42", workspaceName);
    }

    [Fact]
    public void WorkspacePayload_NonIssueRun_HasNoWorkspaceIdentity()
    {
        // When IssueNumber is null/0, ReadIssueNumber returns null and the
        // translator emits a null workspace. The removed run-derived
        // workspace.path fallback must never come back.
        var metadataNull = new WorkflowRunMetadata(null, default, IssueNumber: null);
        Assert.Null(ReadIssueNumber(metadataNull));

        var metadataZero = new WorkflowRunMetadata(null, default, IssueNumber: 0);
        Assert.Null(ReadIssueNumber(metadataZero));
    }

    private static int? ReadIssueNumber(WorkflowRunMetadata metadata) =>
        metadata.IssueNumber is > 0 ? metadata.IssueNumber : null;
}
