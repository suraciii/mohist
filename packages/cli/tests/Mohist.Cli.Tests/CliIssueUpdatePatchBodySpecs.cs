using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueUpdatePatchBodySpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateIssueUpdateSetup(string? activeProjectId = "proj_abc", string? projectResponseBody = null)
    {
        return CliTestFactory.Create(async (req, _) =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/labels", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { "module", "stream" },
                });
            }
            if (path.Contains("/issues/", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_1",
                        number = 1,
                        title = "An issue",
                        body = "original body",
                        labels = projectResponseBody is null
                            ? new Dictionary<string, string>()
                            : JsonSerializer.Deserialize<Dictionary<string, string>>(projectResponseBody),
                    },
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        }, activeProjectId);
    }

    [Fact]
    public async Task IssueUpdate_BodyFile_WithoutLabel_OmitsLabelsFromPatchBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();
        fs.AddFile("/tmp/body.md", "Updated body content");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--body-file", "/tmp/body.md"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/projects/proj_abc/issues/1", patchReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.Equal("Updated body content", body["body"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("labels"));
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("priority"));
        Assert.False(body.AsObject().ContainsKey("isDraft"));
    }

    [Fact]
    public async Task IssueUpdate_BodyInline_WithoutLabel_OmitsLabelsFromPatchBody()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--body", "Inline body"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.Equal("Inline body", body["body"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("labels"));
        Assert.False(body.AsObject().ContainsKey("title"));
    }

    [Fact]
    public async Task IssueUpdate_LabelOnly_PatchBodyOnlyContainsLabels()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "-l", "stream=backend", "-l", "module=auth"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.NotNull(body["labels"]);
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("body"));
        Assert.False(body.AsObject().ContainsKey("priority"));
        var labels = body["labels"] as JsonObject;
        Assert.Equal("backend", labels!["stream"]?.GetValue<string>());
        Assert.Equal("auth", labels["module"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_LabelOnly_PreservesTitleAndBody()
    {
        var currentJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["stream"] = "frontend",
        });
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup(projectResponseBody: currentJson);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "-l", "stream=backend"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("body"));
        Assert.False(body.AsObject().ContainsKey("priority"));
    }

    [Fact]
    public async Task IssueUpdate_NoOptionalFlags_SendsPatchWithNoOptionalFieldKeys()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("body"));
        Assert.False(body.AsObject().ContainsKey("labels"));
        Assert.False(body.AsObject().ContainsKey("priority"));
        Assert.False(body.AsObject().ContainsKey("isDraft"));
    }

    [Fact]
    public async Task IssueUpdate_BodyAndBodyFile_ConflictExitsWithUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();
        fs.AddFile("/tmp/body.md", "From file");

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "1", "--body", "Inline", "--body-file", "/tmp/body.md"],
            output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("--body, --body-file", error.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueUpdate_BodyStdin_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "1", "--body-stdin"],
            output, error, fs, executor,
            standardInput: new StringReader("from stdin"));

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("--body-stdin", error.ToString());
    }

    [Fact]
    public async Task IssueUpdate_BodyFileDash_ReadsStandardInput()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "1", "--body-file", "-"],
            output, error, fs, executor,
            standardInput: new StringReader("from stdin"));

        Assert.Equal(0, exitCode);
        var patch = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.Equal("from stdin", JsonNode.Parse(patch.Body!)!["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_TitleOnly_PatchBodyOnlyContainsTitle()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--title", "New title"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.Equal("New title", body["title"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("labels"));
        Assert.False(body.AsObject().ContainsKey("body"));
        Assert.False(body.AsObject().ContainsKey("priority"));
    }

    [Fact]
    public async Task IssueUpdate_PriorityOnly_PatchBodyOnlyContainsPriority()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--priority", "p1"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.Equal("p1", body["priority"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("labels"));
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("body"));
    }

    [Fact]
    public async Task IssueUpdate_ReadyFlag_PatchBodyContainsIsDraftFalse()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--ready"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.False(body["isDraft"]?.GetValue<bool>());
        Assert.False(body.AsObject().ContainsKey("labels"));
    }

    [Fact]
    public async Task IssueUpdate_DraftFlag_PatchBodyContainsIsDraftTrue()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--draft"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.True(body["isDraft"]?.GetValue<bool>());
        Assert.False(body.AsObject().ContainsKey("labels"));
    }

    [Fact]
    public async Task IssueUpdate_NoDraftFlag_PatchBodyOmitsIsDraft()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueUpdateSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "--title", "X"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("isDraft"));
    }
}
