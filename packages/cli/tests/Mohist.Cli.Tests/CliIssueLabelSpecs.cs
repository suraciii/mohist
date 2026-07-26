using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueLabelSpecs
{
    private static (RecordingHttpHandler Handler, HttpClient Http, StringWriter Output, StringWriter Error, FakeFileSystem Fs, FakeCommandExecutor Executor)
        CreateIssueLabelSetup(string? activeProjectId = "proj_abc", string? projectResponseBody = null)
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
    public void Parse_SingleSet_ProducesSetEntry()
    {
        var result = LabelDelta.Parse(["stream=frontend"]);
        Assert.True(result.IsValid);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(LabelDelta.Operation.Set, entry.Op);
        Assert.Equal("stream", entry.Key);
        Assert.Equal("frontend", entry.Value);
    }

    [Fact]
    public void Parse_MultipleTokens_AllAccepted()
    {
        var result = LabelDelta.Parse(["stream=frontend", "module=auth"]);
        Assert.True(result.IsValid);
        Assert.Equal(2, result.Entries.Length);
        Assert.Contains(result.Entries, e => e.Key == "stream" && e.Value == "frontend");
        Assert.Contains(result.Entries, e => e.Key == "module" && e.Value == "auth");
    }

    [Fact]
    public void Parse_RemoveToken_ProducesRemoveEntry()
    {
        var result = LabelDelta.Parse(["-stream"]);
        Assert.True(result.IsValid);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(LabelDelta.Operation.Remove, entry.Op);
        Assert.Equal("stream", entry.Key);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void Parse_MixedSetAndRemove_BothAccepted()
    {
        var result = LabelDelta.Parse(["stream=backend", "-module"]);
        Assert.True(result.IsValid);
        Assert.Equal(2, result.Entries.Length);
        Assert.Contains(result.Entries, e => e.Op == LabelDelta.Operation.Set && e.Key == "stream" && e.Value == "backend");
        Assert.Contains(result.Entries, e => e.Op == LabelDelta.Operation.Remove && e.Key == "module");
    }

    [Fact]
    public void Parse_EmptyValue_FailsWithError()
    {
        var result = LabelDelta.Parse(["stream="]);
        Assert.False(result.IsValid);
        Assert.Contains("stream", result.Error);
    }

    [Fact]
    public void Parse_MissingKeyBeforeEquals_FailsWithError()
    {
        var result = LabelDelta.Parse(["=x"]);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_InvalidKeyUppercase_FailsWithError()
    {
        var result = LabelDelta.Parse(["Bad-Key=foo"]);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_ConsecutiveInteriorDashes_Accepts()
    {
        var result = LabelDelta.Parse(["stream--auth=frontend"]);
        Assert.True(result.IsValid);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("stream--auth", entry.Key);
    }

    [Fact]
    public void Parse_EmptyRemoveToken_FailsWithError()
    {
        var result = LabelDelta.Parse(["-"]);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Apply_SetUpsert_ReplacesValueForExistingKey()
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream"] = "frontend",
            ["module"] = "auth",
        };
        var entries = new[] { new LabelDelta.Entry(LabelDelta.Operation.Set, "stream", "backend") };
        var next = LabelDelta.Apply(entries, current);
        Assert.Equal("backend", next["stream"]);
        Assert.Equal("auth", next["module"]);
    }

    [Fact]
    public void Apply_Remove_DeletesKey()
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream"] = "frontend",
            ["module"] = "auth",
        };
        var entries = new[] { new LabelDelta.Entry(LabelDelta.Operation.Remove, "stream", null) };
        var next = LabelDelta.Apply(entries, current);
        Assert.False(next.ContainsKey("stream"));
        Assert.Equal("auth", next["module"]);
    }

    [Fact]
    public void Apply_RemoveMissingKey_IsIdempotent()
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stream"] = "frontend",
        };
        var entries = new[] { new LabelDelta.Entry(LabelDelta.Operation.Remove, "missing", null) };
        var next = LabelDelta.Apply(entries, current);
        Assert.Single(next);
        Assert.Equal("frontend", next["stream"]);
    }

    [Fact]
    public void ValidateFilterToken_KeyValue_Accepts()
    {
        Assert.Null(LabelDelta.ValidateFilterToken("stream=frontend"));
    }

    [Fact]
    public void ValidateFilterToken_WithoutEquals_Fails()
    {
        Assert.NotNull(LabelDelta.ValidateFilterToken("frontend"));
    }

    [Fact]
    public async Task IssueCreate_KeyValueLabel_SendsLabelsObject()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "My new issue", "-b", "Body", "-l", "stream=frontend"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Last();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_abc/issues", request.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        var labels = body["labels"] as JsonObject;
        Assert.NotNull(labels);
        Assert.Equal("frontend", labels!["stream"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_MultipleLabels_AllSentInObject()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "create", "Multi", "-b", "Body", "-l", "stream=frontend", "-l", "module=auth"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var body = JsonNode.Parse(handler.Requests.Last().Body!)!;
        var labels = body["labels"] as JsonObject;
        Assert.NotNull(labels);
        Assert.Equal("frontend", labels!["stream"]?.GetValue<string>());
        Assert.Equal("auth", labels["module"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_MalformedLabel_ReturnsUsageFailureWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "Bad", "-l", "=x"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEqual("", error.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueCreate_InvalidKey_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "Bad", "-l", "Bad-Key=foo"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEqual("", error.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueCreate_EmptyValue_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();
        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "create", "Bad", "-l", "stream="], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEqual("", error.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueUpdate_SetLabel_ReadsCurrentAndSendsMergedFullMap()
    {
        var currentJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["stream"] = "frontend",
            ["module"] = "auth",
        });
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup(projectResponseBody: currentJson);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "-l", "stream=backend"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
        var getReq = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, getReq.Method);
        Assert.Equal("/api/projects/proj_abc/issues/1", getReq.RequestUri?.PathAndQuery);
        var patchReq = handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, patchReq.Method);
        Assert.Equal("/api/projects/proj_abc/issues/1", patchReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(patchReq.Body!)!;
        var labels = body["labels"] as JsonObject;
        Assert.NotNull(labels);
        Assert.Equal("backend", labels!["stream"]?.GetValue<string>());
        Assert.Equal("auth", labels["module"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_RemoveLabel_ReadsCurrentAndSendsMapWithoutRemovedKey()
    {
        var currentJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["stream"] = "frontend",
            ["module"] = "auth",
        });
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup(projectResponseBody: currentJson);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "-l", "-stream"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        var labels = body["labels"] as JsonObject;
        Assert.NotNull(labels);
        Assert.False(labels!.ContainsKey("stream"));
        Assert.Equal("auth", labels["module"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_RemoveMissingKey_IsIdempotent()
    {
        var currentJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["module"] = "auth",
        });
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup(projectResponseBody: currentJson);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "-l", "-stream"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        var labels = body["labels"] as JsonObject;
        Assert.NotNull(labels);
        Assert.False(labels!.ContainsKey("stream"));
        Assert.Equal("auth", labels["module"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_MalformedLabel_ExitsWithErrorAndNoPatch()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "1", "-l", "=x"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.NotEqual("", error.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueList_LabelFilter_AppendsKeyValueQueryString()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "-l", "stream=frontend"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var req = handler.Requests.Last();
        Assert.Equal(HttpMethod.Get, req.Method);
        var query = req.RequestUri?.Query ?? "";
        Assert.Contains("label=", query);
        Assert.Contains("stream%3Dfrontend", query);
    }

    [Fact]
    public async Task IssueList_MultipleLabelFilters_AppendsRepeatedQueryStringKeys()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "list", "-l", "stream=frontend", "-l", "module=auth"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exitCode);
        var query = handler.Requests.Last().RequestUri?.Query ?? "";
        Assert.Contains("label=stream%3Dfrontend", query);
        Assert.Contains("label=module%3Dauth", query);
    }

    [Fact]
    public async Task IssueList_MalformedFilter_ExitsWithError()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "-l", "frontend"], output, error, fs, executor);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.NotEqual("", error.ToString());
    }


    [Fact]
    public async Task LabelList_Table_RendersDefinitions()
    {
        var (handler, http, output, error, fs, executor) = CreateIssueLabelSetup();
        handler.SetResponder(async (_, _) => RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[]
            {
                new
                {
                    key = "module",
                    description = "Classifies the subsystem",
                    origin = "User",
                    supportedValues = (string[]?)new[] { "auth", "ui" },
                },
                new
                {
                    key = "refactor",
                    description = "Technical refactoring that does not change observable behavior",
                    origin = "System",
                    supportedValues = (string[]?)null,
                },
            },
        }));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["label", "list",], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("key", text);
        Assert.Contains("description", text);
        Assert.Contains("origin", text);
        Assert.Contains("module", text);
        Assert.Contains("Classifies the subsystem", text);
        Assert.Contains("refactor", text);
        Assert.Contains("User", text);
        Assert.Contains("System", text);
    }

    [Fact]
    public async Task IssueList_RendersLabelsInKeyValueForm()
    {
        var handler = new RecordingHttpHandler(async (_, _) => RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[]
            {
                new
                {
                    number = 1,
                    title = "First",
                    workflowStage = "backlog",
                    status = "active",
                    priority = "p2",
                    labels = new Dictionary<string, string>
                    {
                        ["stream"] = "frontend",
                        ["module"] = "auth",
                    },
                },
            },
        }));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("stream=frontend", text);
        Assert.Contains("module=auth", text);
    }
}
