using System.Text;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.Tests.Agent.Api;

[Trait("level", "L0")]
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
    public async Task BindAsync_ParsesSupportedContextValuesWithoutStartingAHost()
    {
        var body = await BindAsync("{\"prompt\":\"review\",\"context\":{\"issueNumber\":7,\"epicNumber\":8,\"repository\":\"repo\",\"workspace\":\"work\",\"workspacePath\":\"/virtual/work\",\"targetId\":\"target\"}}");

        Assert.Null(body.BindingError);
        Assert.NotNull(body.Context);
        Assert.Equal(7, body.Context!.IssueNumber);
        Assert.Equal(8, body.Context.EpicNumber);
        Assert.Equal("repo", body.Context.Repository);
        Assert.Equal("work", body.Context.Workspace);
        Assert.Equal("/virtual/work", body.Context.WorkspacePath);
        Assert.Equal("target", body.Context.TargetId);
    }

    [Fact]
    public async Task BindAsync_RejectsOpaqueContextNumbersBeforeValidation()
    {
        var body = await BindAsync("{\"prompt\":\"review\",\"context\":{\"epicNumber\":\"epic-7\"}}");

        Assert.Contains("context.epicNumber must be an integer", body.BindingError);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 0)]
    public async Task ValidateContextAsync_RejectsNonPositiveReferencesWithoutQuerying(
        int? epicNumber,
        int? issueNumber)
    {
        var result = await AgentSessionLaunchRoutes.ValidateContextAsync(
            new AgentSessionLaunchContextRef(IssueNumber: issueNumber, EpicNumber: epicNumber),
            "project-1",
            issueQuerier: null!,
            epicQuerier: null!,
            grains: null!);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
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
