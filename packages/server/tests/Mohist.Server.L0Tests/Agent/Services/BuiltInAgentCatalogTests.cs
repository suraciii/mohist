using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Agent.Services;

public sealed class BuiltInAgentCatalogTests
{
    [Fact]
    public void Mohist_slack_is_reserved_and_resolves_from_embedded_instructions()
    {
        Assert.True(BuiltInAgentCatalog.IsReservedName("MOHIST-SLACK"));
        Assert.False(BuiltInAgentCatalog.IsReservedName("release-helper"));
        Assert.False(BuiltInAgentCatalog.IsReservedName(BuiltInAgentCatalog.MohistPlannerName));

        var agent = BuiltInAgentCatalog.Resolve(BuiltInAgentCatalog.MohistSlackName);

        Assert.Equal(BuiltInAgentCatalog.MohistSlackName, agent.Name);
        Assert.Equal(BuiltInAgentCatalog.MohistSlackProjectId, agent.ProjectId);
        Assert.Equal("active", agent.Status);
        Assert.Contains("authenticated", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credentials", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mohistManagerTool", agent.Instructions, StringComparison.Ordinal);
        Assert.Contains("natural language", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo` CLI capabilities", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not delete", agent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xoxb-", agent.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_lists_all_server_builtin_definitions()
    {
        var resolver = new BuiltInAgentResolver();

        var resolved = await resolver.ResolveAsync("mohist-slack");
        var listed = await resolver.ListAsync();

        Assert.NotNull(resolved);
        Assert.Equal(4, listed.Count);
        Assert.Contains(listed, agent => agent.Name == BuiltInAgentCatalog.MohistPlannerName);
        Assert.Contains(listed, agent => agent.Name == BuiltInAgentCatalog.MohistBuilderName);
        Assert.Contains(listed, agent => agent.Name == BuiltInAgentCatalog.MohistReviewerName);
        Assert.All(listed, agent => Assert.False(string.IsNullOrWhiteSpace(agent.Instructions)));
    }

    [Theory]
    [InlineData(BuiltInAgentCatalog.MohistPlannerName)]
    [InlineData(BuiltInAgentCatalog.MohistBuilderName)]
    [InlineData(BuiltInAgentCatalog.MohistReviewerName)]
    public void Workflow_builtin_resolves_in_the_calling_project(string name)
    {
        var agent = BuiltInAgentCatalog.Resolve(name, "project-1");

        Assert.Equal("project-1", agent.ProjectId);
        Assert.Equal(name, agent.Name);
        Assert.Equal("opencode", agent.AgentConfig?.GetProperty("runtime").GetString());
    }
}
