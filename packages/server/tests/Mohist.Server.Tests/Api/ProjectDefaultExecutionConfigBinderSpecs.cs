using System.Text;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.Tests.Api;

[Trait("level", "L0")]
public sealed class ProjectDefaultExecutionConfigBinderSpecs
{
    [Fact]
    public async Task BindAsync_ReadsTheClosedExecutionFieldSet()
    {
        var body = await BindAsync("{\"runtime\":\"pi\",\"model\":\"openai/gpt-5.6\",\"variant\":\"high\"}");

        Assert.Equal("pi", body.Runtime);
        Assert.Equal("openai/gpt-5.6", body.Model);
        Assert.Equal("high", body.Variant);
        Assert.Empty(body.UndeclaredFields);
    }

    [Fact]
    public async Task BindAsync_RecordsUndeclaredFieldsWithoutChangingKnownValues()
    {
        var body = await BindAsync("{\"runtime\":\"pi\",\"model\":\"openai/gpt-5.6\",\"instructions\":\"nope\"}");

        Assert.Equal("pi", body.Runtime);
        Assert.Equal("openai/gpt-5.6", body.Model);
        Assert.Null(body.Variant);
        Assert.Equal(["instructions"], body.UndeclaredFields);
    }

    [Fact]
    public async Task BindAsync_PreservesNullAndMissingValuesAsUnset()
    {
        var body = await BindAsync("{\"runtime\":null}");

        Assert.Null(body.Runtime);
        Assert.Null(body.Model);
        Assert.Null(body.Variant);
        Assert.Empty(body.UndeclaredFields);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"runtime\":42}")]
    [InlineData("{\"model\":false}")]
    public async Task BindAsync_ReturnsNullForMalformedFieldShapes(string json)
    {
        Assert.Null(await BindAsyncNullable(json));
    }

    private static async Task<ProjectDefaultExecutionConfigBody> BindAsync(string json) =>
        await BindAsyncNullable(json)
            ?? throw new InvalidOperationException("execution config binder returned null");

    private static async Task<ProjectDefaultExecutionConfigBody?> BindAsyncNullable(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await ProjectDefaultExecutionConfigBody.BindAsync(context);
    }
}
