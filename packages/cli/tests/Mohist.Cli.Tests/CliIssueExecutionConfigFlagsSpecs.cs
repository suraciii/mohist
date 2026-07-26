using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueExecutionConfigFlagsSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateIssueCommandSetup(string? activeProjectId = "proj_abc")
    {
        return CliTestFactory.Create(async (req, _) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_1",
                        number = 1,
                        title = "T",
                        isDraft = true,
                    },
                }, HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Patch)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_1",
                        number = 1,
                    },
                });
            }
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_1",
                        number = 1,
                        title = "An issue",
                        labels = new Dictionary<string, string>(),
                    },
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        }, activeProjectId);
    }

    [Fact]
    public async Task IssueCreate_RepoFlag_IsSentInPostBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My issue", "--body", "Hello", "--repo", "feature-repo"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("feature-repo", body["repositoryName"]?.GetValue<string>());
        Assert.Equal("My issue", body["title"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_NoRepositoryFlag_OmitsRepositoryNameFromPostBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My issue", "--body", "Hello"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("repositoryName"));
    }

    [Fact]
    public async Task IssueCreate_StageModelsInlineJson_IsSentInPostBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "create", "My issue", "--body", "Hello", "--stage-models", "{\"plan\":\"anthropic/claude-sonnet\"}"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        var stageModels = body["stageModels"] as JsonObject;
        Assert.NotNull(stageModels);
        Assert.Equal("anthropic/claude-sonnet", stageModels!["plan"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_StageModelsFromFile_IsReadAndSentAsParsedJson()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();
        fs.AddFile("/tmp/models.json", "{\"plan\":\"anthropic/claude-sonnet\",\"check\":\"openai/gpt-5\"}");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My issue", "--body", "Hello", "--stage-models", "@/tmp/models.json"],
            output, error, fs, executor);

        Assert.True(exitCode == 0, $"exit={exitCode} stderr={error} stdout={output}");
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        var stageModels = body["stageModels"] as JsonObject;
        Assert.NotNull(stageModels);
        Assert.Equal("anthropic/claude-sonnet", stageModels!["plan"]?.GetValue<string>());
        Assert.Equal("openai/gpt-5", stageModels["check"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_StageModelVariantsFromFile_IsReadAndSentAsParsedJson()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();
        fs.AddFile("/tmp/variants.json", "{\"plan\":\"max\",\"check\":\"high\"}");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My issue", "--body", "Hello", "--stage-model-variants", "@/tmp/variants.json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        var stageModelVariants = body["stageModelVariants"] as JsonObject;
        Assert.NotNull(stageModelVariants);
        Assert.Equal("max", stageModelVariants!["plan"]?.GetValue<string>());
        Assert.Equal("high", stageModelVariants["check"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_StageModelsInvalidJson_ExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My issue", "--body", "Hello", "--stage-models", "not-json"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("--stage-models", error.ToString());
        Assert.Contains("invalid JSON", error.ToString());
    }

    [Fact]
    public async Task IssueCreate_StageModelsFileMissing_ExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My issue", "--body", "Hello", "--stage-models", "@/tmp/does-not-exist.json"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        var stderr = error.ToString();
        Assert.Contains("--stage-models", stderr);
    }

    [Fact]
    public async Task IssueCreate_NoStageModelFlags_OmitsFieldsFromPostBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My issue", "--body", "Hello"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("stageModels"));
        Assert.False(body.AsObject().ContainsKey("stageModelVariants"));
    }

    [Fact]
    public async Task IssueUpdate_StageModelsInlineJson_IsSentInPatchBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "1", "--stage-models", "{\"plan\":\"openai/gpt-5\"}"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        var stageModels = body["stageModels"] as JsonObject;
        Assert.NotNull(stageModels);
        Assert.Equal("openai/gpt-5", stageModels!["plan"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("body"));
        Assert.False(body.AsObject().ContainsKey("labels"));
    }

    [Fact]
    public async Task IssueUpdate_StageModelVariantsFromFile_IsReadAndSentAsParsedJson()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();
        fs.AddFile("/tmp/variants.json", "{\"plan\":\"max\",\"check\":\"high\"}");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "1", "--stage-model-variants", "@/tmp/variants.json"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        var stageModelVariants = body["stageModelVariants"] as JsonObject;
        Assert.NotNull(stageModelVariants);
        Assert.Equal("max", stageModelVariants!["plan"]?.GetValue<string>());
        Assert.Equal("high", stageModelVariants["check"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_RepoFlag_SendsRepositoryNameAndReportsCanonicalTarget()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();
        handler.SetResponder((req, _) =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { number = 1, repositoryName = "web" },
                }));
            }

            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--repo", "WEB", "--json", "number,repositoryName"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patch.Body!)!;
        Assert.Equal("WEB", body["repositoryName"]?.GetValue<string>());
        Assert.Contains("repositoryName", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("web", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueUpdate_StageModelsInvalidJson_ExitsWithCodeOne()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--stage-models", "not-json"],
            output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("--stage-models", error.ToString());
        Assert.Contains("invalid JSON", error.ToString());
    }

    [Fact]
    public async Task IssueUpdate_StageModelsWithoutFileFlag_OmitsFieldFromPatchBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--title", "X"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("stageModels"));
        Assert.False(body.AsObject().ContainsKey("stageModelVariants"));
    }

    [Fact]
    public async Task IssueCreate_AllExecutionConfigFlags_AreSentInPostBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueCommandSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            [
                "issue", "create", "My issue",
                "--body", "Hello",
                "--repo", "feature-repo",
                "--stage-models", "{\"plan\":\"anthropic/claude-sonnet\"}",
                "--stage-model-variants", "{\"plan\":\"max\"}",
            ],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("feature-repo", body["repositoryName"]?.GetValue<string>());
        var stageModels = body["stageModels"] as JsonObject;
        Assert.Equal("anthropic/claude-sonnet", stageModels!["plan"]?.GetValue<string>());
        var stageModelVariants = body["stageModelVariants"] as JsonObject;
        Assert.Equal("max", stageModelVariants!["plan"]?.GetValue<string>());
    }
}
