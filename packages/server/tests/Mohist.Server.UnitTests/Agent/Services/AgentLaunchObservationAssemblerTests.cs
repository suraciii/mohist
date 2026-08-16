using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentLaunchObservationAssemblerTests
{
    [Theory]
    [InlineData(AgentJobStatus.Unknown, true, "recovering")]
    [InlineData(AgentJobStatus.Unknown, false, "unknown")]
    [InlineData(AgentJobStatus.Running, true, "running")]
    [InlineData(AgentJobStatus.Failed, true, "failed")]
    public void ToJobStatusString_ProjectsRecoveringWithoutChangingPersistedUnknown(
        AgentJobStatus status,
        bool isRecovering,
        string expected)
    {
        Assert.Equal(expected,
            AgentLaunchObservationAssembler.ToJobStatusString(status, isRecovering));
    }
}
