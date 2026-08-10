using Mohist.Server.Api;
using Mohist.Server.Runner.Services.SignalR;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.SignalR;

public class RunnerIdentityViewTests
{
    [Fact]
    public void FromRuntimeIdentity_WhenExactIdentityDoesNotMatch_ReportsOfflineAndDisconnected()
    {
        var view = RunnerIdentityView.FromRuntimeIdentity(new RunnerRuntimeConnection(
            "runner-1",
            "2",
            "0123456789abcdef0123456789abcdef01234567",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            IsOnline: false,
            ConnectionId: "physical-connection"));

        Assert.Equal("offline", view.Status);
        Assert.Equal("disconnected", view.ConnectionState);
    }

    [Fact]
    public void FromRuntimeIdentity_WhenExactIdentityMatches_ReportsOnlineAndConnected()
    {
        var view = RunnerIdentityView.FromRuntimeIdentity(new RunnerRuntimeConnection(
            "runner-1",
            "2",
            "0123456789abcdef0123456789abcdef01234567",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            IsOnline: true,
            ConnectionId: "physical-connection"));

        Assert.Equal("online", view.Status);
        Assert.Equal("connected", view.ConnectionState);
    }
}
