using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class AgentSessionRetryPolicyTests
{
    [Theory]
    [InlineData(AgentJobFailureReasons.RunnerUnavailable)]
    [InlineData(AgentJobFailureReasons.RunnerLost)]
    [InlineData(AgentJobFailureReasons.ReportTimeout)]
    [InlineData("deadline-exceeded")]
    [InlineData("timeout")]
    [InlineData("generation-drain-timeout")]
    [InlineData("unavailable-runtime")]
    [InlineData("runtime-unavailable")]
    [InlineData("rate-limited")]
    [InlineData("probe-timeout")]
    [InlineData("skill-not-found")]
    [InlineData("retry-safe")]
    public void RecordedRetryableCategory_IsRetryable(string category) =>
        Assert.True(AgentSessionRetryPolicy.IsRetryable(category));

    [Theory]
    [InlineData(AgentJobFailureReasons.WorkspaceUnavailable)]
    [InlineData("invalid-input")]
    [InlineData("permission-required")]
    [InlineData("incompatible-runtime")]
    [InlineData("incompatible-execution-configuration")]
    [InlineData("unsupported_execution_configuration")]
    [InlineData("missing-session")]
    [InlineData("runtime-session-missing")]
    [InlineData("conflict")]
    [InlineData("interrupted")]
    [InlineData("turn-failed")]
    [InlineData("manager-credential-expired")]
    [InlineData("context_exhaustion")]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    [InlineData("retry")]
    public void RecordedPermanentOrUnknownCategory_IsNotRetryable(string category) =>
        Assert.False(AgentSessionRetryPolicy.IsRetryable(category));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AbsentOrEmptyCategory_IsNotRetryable(string? category) =>
        Assert.False(AgentSessionRetryPolicy.IsRetryable(category));

    [Theory]
    [InlineData("Runner-Unavailable")]
    [InlineData("timeout ")]
    [InlineData(" Timeout")]
    public void CategoryComparisonIsExactOrdinal(string category) =>
        Assert.False(AgentSessionRetryPolicy.IsRetryable(category));

    [Fact]
    public void ReasonTextDoesNotChangeCategoryDecision()
    {
        Assert.False(AgentSessionRetryPolicy.IsRetryable("invalid-input"));
        Assert.True(AgentSessionRetryPolicy.IsRetryable("retry-safe"));
    }
}
