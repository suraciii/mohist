using Mohist.Server.Agent.Services;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Agent.Services;

public sealed class SlackManagerAgentToolsTests
{
    [Fact]
    public void Catalog_contains_only_non_destructive_manager_actions()
    {
        Assert.Equal(
            ["claim-owner", "create", "diagnostics", "disable", "edit", "enable", "list", "transfer-owner", "view"],
            SlackManagerAgentTools.Allowed.OrderBy(value => value).ToArray());
        Assert.All(
            ["remove-binding", "delete", "permanent-delete", "configure", "rotate-credentials"],
            tool =>
            {
                Assert.True(SlackManagerAgentTools.IsForbidden(tool));
                Assert.False(SlackManagerAgentTools.IsAllowed(tool));
                Assert.False(SlackManagerAgentTools.IsAllowed(tool));
            });
    }

    [Fact]
    public void Unknown_tools_do_not_get_a_manager_authorization_decision()
    {
        Assert.False(SlackManagerAgentTools.IsAllowed("run-sql"));
        Assert.Null(SlackManagerAgentTools.CapabilityForTool("run-sql"));
    }
}
