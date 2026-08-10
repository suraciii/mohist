using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Api;

public sealed class RunnerRoutesNeedsFreshRuntimeSessionTests
{
    [Theory]
    [InlineData("failed")]
    [InlineData("FAILED")]
    [InlineData("aborted")]
    [InlineData("cancelled")]
    [InlineData("timeout")]
    public void RuntimeSessionWithRetryableTerminalStatusRequiresFreshSession(string status)
    {
        Assert.True(RunnerRoutes.NeedsFreshRuntimeSession("runtime-old", status));
    }

    [Theory]
    [InlineData(null, "failed")]
    [InlineData("", "failed")]
    [InlineData("   ", "failed")]
    [InlineData("runtime-old", null)]
    [InlineData("runtime-old", "completed")]
    [InlineData("runtime-old", "active")]
    public void MissingRuntimeSessionOrNonTerminalFailureDoesNotRequireFreshSession(
        string? runtimeSessionId,
        string? status)
    {
        Assert.False(RunnerRoutes.NeedsFreshRuntimeSession(runtimeSessionId, status));
    }
}
