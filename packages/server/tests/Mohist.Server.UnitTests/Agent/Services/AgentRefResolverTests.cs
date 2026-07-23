using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentRefResolverTests
{
    [Fact]
    public async Task NonPrefixedReferencePrefersNameOverLegacyId()
    {
        var calls = new List<string>();

        var result = await AgentRefResolver.ResolveAsync(
            "reviewer",
            id =>
            {
                calls.Add($"id:{id}");
                return Task.FromResult<string?>("id-match");
            },
            name =>
            {
                calls.Add($"name:{name}");
                return Task.FromResult<string?>("name-match");
            });

        Assert.Equal("name-match", result);
        Assert.Equal(["name:reviewer"], calls);
    }

    [Fact]
    public async Task PrefixedReferenceUsesOnlyIdLookup()
    {
        var calls = new List<string>();

        var result = await AgentRefResolver.ResolveAsync(
            "agent_123",
            id =>
            {
                calls.Add($"id:{id}");
                return Task.FromResult<string?>("id-match");
            },
            name =>
            {
                calls.Add($"name:{name}");
                return Task.FromResult<string?>("name-match");
            });

        Assert.Equal("id-match", result);
        Assert.Equal(["id:agent_123"], calls);
    }
}
