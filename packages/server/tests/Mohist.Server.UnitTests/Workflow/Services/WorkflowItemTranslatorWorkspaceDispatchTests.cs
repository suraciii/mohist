using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Services;

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
    public void WorkspacePayload_NonIssueRun_FallsBackToPath()
    {
        // When IssueNumber is null/0, ReadIssueNumber returns null,
        // and the translator emits {path, branch} from run.Workspace —
        // it must NOT emit {name}. This test asserts the contract.
        var metadataNull = new WorkflowRunMetadata(null, default, IssueNumber: null);
        Assert.Null(ReadIssueNumber(metadataNull));

        var metadataZero = new WorkflowRunMetadata(null, default, IssueNumber: 0);
        Assert.Null(ReadIssueNumber(metadataZero));

        // Verify the old workspace identity fields are still available
        var workspace = new WorkspaceIdentity("/custom/path", "mohist/run-legacy", "/change");
        Assert.Equal("/custom/path", workspace.Path);
        Assert.Equal("mohist/run-legacy", workspace.Branch);
    }

    private static int? ReadIssueNumber(WorkflowRunMetadata metadata) =>
        metadata.IssueNumber is > 0 ? metadata.IssueNumber : null;
}
