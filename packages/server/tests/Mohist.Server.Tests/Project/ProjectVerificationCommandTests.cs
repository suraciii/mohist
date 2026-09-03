using System.Text;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Project;

[Trait("level", "L0")]
public sealed class ProjectVerificationCommandTests
{
    [Fact]
    public void Validate_RejectsMissingAndNulCommands()
    {
        Assert.NotNull(ProjectVerificationCommand.Validate("   "));
        Assert.NotNull(ProjectVerificationCommand.Validate("echo\0verify"));
    }

    [Fact]
    public void Validate_RejectsCommandsOverUtf8Limit()
    {
        var command = new string('界', ProjectVerificationCommand.MaxUtf8Bytes / Encoding.UTF8.GetByteCount("界") + 1);
        Assert.Contains("UTF-8", ProjectVerificationCommand.Validate(command));
    }

    [Fact]
    public void Require_PreservesAcceptedText()
    {
        const string command = "  npm run verify\n";
        Assert.Equal(command, ProjectVerificationCommand.Require(command));
    }

    [Fact]
    public void StateUpgrade_PreservesUnknownHistoricalLaneProperties()
    {
        const string state = "{\"boundWorkflowDefinitionJson\":\"{\\\"stages\\\":[]}\",\"stages\":[{\"tasks\":[{\"id\":\"verify\",\"lane\":{\"laneId\":\"verify\"}}]}]}";
        var upgraded = WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(state);

        Assert.Contains("\"lane\"", upgraded);
        Assert.Contains("boundWorkflowDefinitionJson", upgraded);
        Assert.Equal(upgraded, WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(upgraded));
    }
}
