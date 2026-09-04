using Mohist.Server.Infrastructure;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class ManagerAgentDefaultProfileResolverTests
{
    [Fact]
    public void Resolve_UsesTheCanonicalPiDefault()
    {
        var profile = new ManagerAgentDefaultProfileResolver().Resolve();

        Assert.Equal(AgentConfigSchema.DefaultRuntime, profile.Runtime);
        Assert.Equal(AgentConfigSchema.PiRuntime, profile.ToAgentConfig().GetProperty("runtime").GetString());
    }
}
