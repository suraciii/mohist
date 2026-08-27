using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliIssueNoWorkflowSpecs
{
    [Fact]
    public async Task IssueCreate_NoWorkflow_SendsExplicitSelection()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.Method == HttpMethod.Post
                ? RecordingHttpHandler.Json(new { success = true, data = new { number = 42, title = "External", noWorkflow = true } })
                : null!);

        var exit = await MohistCliCommands.RunAsync(http,
            ["issue", "create", "External", "--body", "Body", "--no-workflow"],
            output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = handler.Requests.Last(request => request.Method == HttpMethod.Post);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.True(body["noWorkflow"]!.GetValue<bool>());
        Assert.False(body.AsObject().ContainsKey("workflowProfileId"));
    }

    [Theory]
    [InlineData("--workflow-profile", "mohist/local")]
    [InlineData("--inherit-workflow-profile", null)]
    public async Task IssueEdit_NoWorkflow_RejectsOtherSelections(string option, string? value)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(_ => null!);
        var args = new List<string> { "issue", "edit", "42", "--no-workflow", option };
        if (value is not null) args.Add(value);

        var exit = await MohistCliCommands.RunAsync(http, [.. args], output, error, fs, executor);

        Assert.Equal(2, exit);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
    }
}
