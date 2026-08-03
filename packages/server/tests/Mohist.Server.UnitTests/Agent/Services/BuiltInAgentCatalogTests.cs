using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class BuiltInAgentCatalogTests
{
    [Fact]
    public void Mohist_slack_is_reserved_and_resolves_from_embedded_instructions()
    {
        Assert.True(BuiltInAgentCatalog.IsReservedName("MOHIST-SLACK"));
        Assert.False(BuiltInAgentCatalog.IsReservedName("release-helper"));

        var agent = BuiltInAgentCatalog.Resolve(BuiltInAgentCatalog.MohistSlackName);

        Assert.Equal(BuiltInAgentCatalog.MohistSlackName, agent.Name);
        Assert.Equal(BuiltInAgentCatalog.MohistSlackProjectId, agent.ProjectId);
        Assert.Equal("active", agent.Status);
        Assert.Contains("authenticated", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credentials", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mohistManagerTool", agent.Instructions, StringComparison.Ordinal);
        Assert.Contains("server validates every tool request", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not delete", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xoxb-", agent.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_lists_only_server_builtin_definitions()
    {
        var resolver = new BuiltInAgentResolver();

        var resolved = await resolver.ResolveAsync("mohist-slack");
        var listed = await resolver.ListAsync();

        Assert.NotNull(resolved);
        Assert.Single(listed);
        Assert.Equal(resolved!.Id, listed[0].Id);
        Assert.Equal(resolved.Name, listed[0].Name);
        Assert.Equal(resolved.Instructions, listed[0].Instructions);
    }
}
