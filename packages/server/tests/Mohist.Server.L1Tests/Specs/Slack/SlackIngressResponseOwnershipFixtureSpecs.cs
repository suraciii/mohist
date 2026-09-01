using System.Text.Json;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Slack;

public sealed class SlackIngressResponseOwnershipFixtureSpecs
{
    [Fact]
    public void Shared_fixture_preserves_the_three_wire_owners()
    {
        using var stream = typeof(SlackIngressResponseOwnershipFixtureSpecs).Assembly
            .GetManifestResourceStream("Mohist.SlackIngressResponseOwnershipFixtures.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream!);
        var root = document.RootElement;

        Assert.Equal("server", root.GetProperty("server").GetProperty("responseOwner").GetString());
        Assert.Equal("adapter", root.GetProperty("adapter").GetProperty("responseOwner").GetString());
        Assert.Equal("none", root.GetProperty("none").GetProperty("responseOwner").GetString());
    }
}
