using System.Text;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.Tests.Api;

[Trait("level", "L0")]
public sealed class RoutingRuleUpdateRequestBinderSpecs
{
    [Fact]
    public async Task BindAsync_RecordsEachCanonicalFieldAndItsValue()
    {
        var request = await BindAsync("{\"name\":\"rule\",\"match\":\"event.type == 'x'\",\"agentId\":\"agent-1\",\"responsePrompt\":\"prompt\",\"continue\":true}");

        Assert.Equal("rule", request.Name);
        Assert.Equal("event.type == 'x'", request.Match);
        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("prompt", request.ResponsePrompt);
        Assert.True(request.Continue);
        Assert.Equal(
            new[] { "name", "match", "agentId", "responsePrompt", "continue" }
                .OrderBy(field => field, StringComparer.Ordinal),
            request.Fields.OrderBy(field => field, StringComparer.Ordinal));
    }

    [Fact]
    public async Task BindAsync_DistinguishesExplicitNullFromAnOmittedField()
    {
        var request = await BindAsync("{\"name\":null,\"continue\":null}");

        Assert.Null(request.Name);
        Assert.Null(request.Continue);
        Assert.Contains("name", request.Fields);
        Assert.Contains("continue", request.Fields);
        Assert.DoesNotContain("match", request.Fields);
    }

    [Fact]
    public async Task BindAsync_UsesOnlyCanonicalLowercasePresenceNames()
    {
        var request = await BindAsync("{\"Name\":\"alias\",\"Continue\":true}");

        Assert.Null(request.Name);
        Assert.Null(request.Continue);
        Assert.Empty(request.Fields);
    }

    [Fact]
    public async Task BindAsync_PreservesAnEmptyObjectAsNoop()
    {
        var request = await BindAsync("{}");

        Assert.Empty(request.Fields);
        Assert.Null(request.Name);
        Assert.Null(request.Match);
        Assert.Null(request.Continue);
    }

    private static async Task<RoutingRuleUpdateRequest> BindAsync(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await RoutingRuleUpdateRequest.BindAsync(context)
            ?? throw new InvalidOperationException("routing rule binder returned null");
    }
}
