using Xunit;

namespace Mohist.Cli.UnitTests;

public class NotifyCommandsTests
{
    [Fact]
    public async Task ProbeHermesHealthAsync_NonAbsoluteBase_ReturnsUnhealthy()
    {
        var result = await NotifyCommands.ProbeHermesHealthAsync("127.0.0.1:8644");

        Assert.False(result.IsHealthy);
        Assert.Equal("invalid url", result.FailureReason);
    }
}
