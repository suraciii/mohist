using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.Tests.Slack;

[Trait("level", "L0")]
public sealed class SlackIngressResponseOwnershipFixtureTests
{
    [Fact]
    public void Shared_fixture_contains_server_adapter_and_none_owners()
    {
        using var stream = typeof(SlackIngressResponseOwnershipFixtureTests).Assembly
            .GetManifestResourceStream("Mohist.SlackIngressResponseOwnershipFixtures.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream!);
        var root = document.RootElement;

        Assert.Equal("server", root.GetProperty("server").GetProperty("responseOwner").GetString());
        Assert.Equal("adapter", root.GetProperty("adapter").GetProperty("responseOwner").GetString());
        Assert.Equal("none", root.GetProperty("none").GetProperty("responseOwner").GetString());
    }
}
