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
                ? RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[]
                    {
                        new
                        {
                            profileId = "delivery/review",
                            name = "Review",
                            sourceProvenance = "custom",
                            agentAction = "mohist/pi",
                            agentRuntime = "pi",
                        },
                    },
                })
                : null!);

        var exit = await MohistCliCommands.RunAsync(http, ["workflow", "list"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains(handler.Requests, r => r.RequestUri?.PathAndQuery == "/api/projects/proj_abc/workflow-profiles");
        Assert.Contains("mohist/pi", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("pi", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewTableExposesAgentActionAndRuntime()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(_ =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    profileId = "mohist/github-pr",
                    name = "GitHub PR",
                    description = "Deliver through a pull request",
                    sourceProvenance = "built-in",
                    isBuiltIn = true,
                    definitionSource = "stages: []\n",
                    agentAction = "mohist/pi",
                    agentRuntime = "pi",
                },
            }));

        var exit = await MohistCliCommands.RunAsync(
            http, ["workflow", "view", "mohist/github-pr"], output, error, fs, executor);

        Assert.Equal(0, exit);
        Assert.Contains("agent action:  mohist/pi", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent runtime: pi", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ViewJsonProjectsNullableAgentFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(_ =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    profileId = "delivery/review",
                    agentAction = (string?)null,
                    agentRuntime = (string?)null,
                },
            }));

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "view", "delivery/review", "--json", "profileId,agentAction,agentRuntime"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        var resource = Assert.IsType<JsonObject>(JsonNode.Parse(output.ToString()));
        Assert.Equal("delivery/review", resource["profileId"]?.GetValue<string>());
        Assert.True(resource.ContainsKey("agentAction"));
        Assert.Null(resource["agentAction"]);
        Assert.True(resource.ContainsKey("agentRuntime"));
        Assert.Null(resource["agentRuntime"]);
    }

    [Fact]
    public async Task EditJsonProjectsTheUpdatedProfileFromTheStandardEnvelope()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(_ =>
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    profileId = "delivery/review",
                    name = "Review",
                    agentAction = "mohist/pi",
                    agentRuntime = "pi",
                },
                validation = new { definitionErrors = Array.Empty<object>(), actionErrors = Array.Empty<object>() },
            }));
        fs.AddFile("profile.yaml", "agentAction: mohist/pi\nstages: []\n");

        var exit = await MohistCliCommands.RunAsync(
            http,
            ["workflow", "edit", "delivery/review", "--file", "profile.yaml", "--json", "profileId,agentAction,agentRuntime"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exit);
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
        var resource = Assert.IsType<JsonObject>(JsonNode.Parse(output.ToString()));
        Assert.Equal("delivery/review", resource["profileId"]?.GetValue<string>());
        Assert.Equal("mohist/pi", resource["agentAction"]?.GetValue<string>());
        Assert.Equal("pi", resource["agentRuntime"]?.GetValue<string>());
        Assert.Empty(error.ToString());
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
