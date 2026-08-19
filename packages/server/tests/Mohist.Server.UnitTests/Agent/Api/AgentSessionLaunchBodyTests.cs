using System.Text;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Api;

public sealed class AgentSessionLaunchBodyTests
{
    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"prompt\":null}", null)]
    [InlineData("{\"prompt\":\"\"}", "")]
    [InlineData("{\"prompt\":\"   \"}", "   ")]
    public async Task BindAsync_PreservesMissingAndBlankPromptShapes(string json, string? expectedPrompt)
    {
        var body = await BindAsync(json);

        Assert.Null(body.BindingError);
        Assert.Equal(expectedPrompt, body.Prompt);
    }

    [Fact]
    public async Task BindAsync_NonStringPrompt_RecordsBindingError()
    {
        var body = await BindAsync("{\"prompt\":42}");

        Assert.Contains("prompt must be a string", body.BindingError);
    }

    [Fact]
    public async Task BindAsync_RecordsUndeclaredExecutionOverrides()
    {
        var body = await BindAsync(
            "{\"prompt\":\"review\",\"runtime\":\"pi\",\"model\":\"openai/gpt-5.6\"}");

        Assert.Null(body.BindingError);
        Assert.Equal(["runtime", "model"], body.UndeclaredFields);
    }

    private static async Task<AgentSessionLaunchBody> BindAsync(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return await AgentSessionLaunchBody.BindAsync(context)
            ?? throw new InvalidOperationException("launch body binder returned null");
    }
}
