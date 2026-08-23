using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackChannelLaunchServiceTests
{
    [Fact]
    public void PreMintSlackLaunchIds_are_deterministic_and_project_scoped()
    {
        var identity = new SlackMessageIdentity("T123", "C-channel", "1710000000.000100");

        var first = SlackChannelLaunchService.PreMintSlackLaunchIds("project-a", identity);
        var replay = SlackChannelLaunchService.PreMintSlackLaunchIds("project-a", identity);
        var otherProject = SlackChannelLaunchService.PreMintSlackLaunchIds("project-b", identity);

        Assert.Equal(first, replay);
        Assert.NotEqual(first.SessionId, otherProject.SessionId);
        Assert.NotEqual(first.InputId, otherProject.InputId);
        Assert.NotEqual(first.TurnId, otherProject.TurnId);
    }
}
