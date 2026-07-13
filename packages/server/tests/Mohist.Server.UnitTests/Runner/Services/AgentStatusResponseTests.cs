using Mohist.Server.Api;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Services;

public class AgentStatusResponseTests
{
    [Fact]
    public void Create_NoOnlineRunners_ReportsUnavailable()
    {
        var status = AgentStatusResponse.Create(
            activeAgents: [],
            runners: Array.Empty<RunnerStatusView>(),
            capacity: new RunnerCapacityView(0, 0));

        Assert.False(status.Running);
        Assert.False(status.RunnerAvailable);
        Assert.False(status.EmbeddedRunnerEnabled);
        Assert.Equal(0, status.Capacity.Active);
        Assert.Equal(0, status.Capacity.Max);
        Assert.Equal("No runner is connected. Start the Mohist runner process.", status.RunnerMessage);
    }
}
