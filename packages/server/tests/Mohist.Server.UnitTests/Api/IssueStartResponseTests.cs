using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public sealed class IssueStartResponseTests
{
    [Fact]
    public async Task StartResponse_ForWorkflowRun_IsAnObjectWithTheAuthoritativeRunId()
    {
        var result = ApiResults.Ok(IssueStartResponse.FromGrainResult(42, "wr_abc123"));

        var (body, status) = await ExecuteAsync(result);

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        var data = body.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal(42, data.GetProperty("number").GetInt32());
        Assert.Equal("wr_abc123", data.GetProperty("workflowRunId").GetString());
    }

    [Fact]
    public async Task StartResponse_ForCompositeIssue_IsAnObjectWithoutInventingAParentRun()
    {
        var result = ApiResults.Ok(IssueStartResponse.FromGrainResult(42, string.Empty));

        var (body, status) = await ExecuteAsync(result);

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        var data = body.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal(42, data.GetProperty("number").GetInt32());
        Assert.False(data.TryGetProperty("workflowRunId", out _));
    }

    private static async Task<(JsonElement Body, int Status)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                options.SerializerOptions.DefaultIgnoreCondition = JSON.Options.DefaultIgnoreCondition;
                foreach (var converter in JSON.Options.Converters)
                    options.SerializerOptions.Converters.Add(converter);
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (document.RootElement.Clone(), context.Response.StatusCode);
    }
}
