using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliWorkflowProfileSurfaceSpecs
{
    [Fact]
    public async Task ListUsesCurrentProjectCollection()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            req.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profiles"
                ? RecordingHttpHandler.Json(new { success = true, data = new[] { new { profileId = "delivery/review", name = "Review" } } })
                : null!);

        var exit = await MohistCliCommands.RunAsync(http, ["workflow", "list"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains(handler.Requests, r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profiles");
    }

    [Fact]
    public async Task ViewYamlPreservesSlashIdAndIsMutuallyExclusiveWithJson()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            RecordingHttpHandler.Json(new { success = true, data = new { profileId = "delivery/review", definitionSource = "stages: []\n" } }));

        var exit = await MohistCliCommands.RunAsync(http, ["workflow", "view", "delivery/review", "--yaml"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("stages: []\n", output.ToString());
        Assert.Equal("/api/projects/proj_abc/workflow-profiles/delivery%2Freview", handler.Requests.Single().RequestUri?.PathAndQuery);

        handler.Requests.Clear();
        output.GetStringBuilder().Clear();
        var conflict = await MohistCliCommands.RunAsync(http, ["workflow", "view", "delivery/review", "--yaml", "--json", "profileId"], output, error, fs, executor);
        Assert.Equal(2, conflict);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProjectDefaultPostsProfileId()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            RecordingHttpHandler.Json(new { success = true, data = new { profileId = "delivery/review" } }));

        var exit = await MohistCliCommands.RunAsync(http, ["project", "workflow", "set-default", "delivery/review"], output, error, fs, executor);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/default", request.RequestUri?.PathAndQuery);
        Assert.Equal("delivery/review", JsonNode.Parse(request.Body!)!["profileId"]!.GetValue<string>());
    }

    [Fact]
    public async Task IssueEditInheritClearsSelectionAndConflictingFlagsAreLocal()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();
        var clearExit = await MohistCliCommands.RunAsync(http, ["issue", "edit", "42", "--inherit-workflow-profile"], output, error, fs, executor);
        Assert.Equal(0, clearExit);
        var patch = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.True(JsonNode.Parse(patch.Body!)!.AsObject().ContainsKey("workflowProfileId"));
        Assert.Null(JsonNode.Parse(patch.Body!)!["workflowProfileId"]);

        handler.Requests.Clear();
        var conflict = await MohistCliCommands.RunAsync(http, ["issue", "edit", "42", "--workflow-profile", "delivery/review", "--inherit-workflow-profile"], output, error, fs, executor);
        Assert.Equal(2, conflict);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PromptCommandsUseRetainedPromptRoutes()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
            RecordingHttpHandler.Json(new { success = true, data = new { key = "plan", body = "Plan" } }));

        var exit = await MohistCliCommands.RunAsync(http, ["project", "workflow", "prompt", "set", "plan", "--body", "Plan"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Equal("/api/projects/proj_abc/workflow-profile/prompts/plan", Assert.Single(handler.Requests).RequestUri?.PathAndQuery);
    }
}
