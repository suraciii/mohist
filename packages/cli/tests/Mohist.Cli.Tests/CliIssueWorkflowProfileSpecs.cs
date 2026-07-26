using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliIssueWorkflowProfileSpecs
{
    [Fact]
    public async Task IssueCreate_WithWorkflowProfileFlag_SendsWorkflowProfileIdInPostBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_42",
                        number = 42,
                        title = "T",
                        workflowProfileId = "mohist/github-pr",
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "create", "Use PR workflow", "--body", "Body content", "--workflow-profile", "mohist/github-pr"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("/api/projects/proj_abc/issues", postReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.Equal("mohist/github-pr", body["workflowProfileId"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_WithoutWorkflowProfileFlag_OmitsWorkflowProfileIdFromPostBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new { id = "issue_42", number = 42, title = "T" },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "create", "Plain issue", "--body", "Body content"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var postReq = handler.Requests.Last(r => r.Method == HttpMethod.Post);
        var body = JsonNode.Parse(postReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("workflowProfileId"));
    }

    [Fact]
    public async Task IssueShow_RendersEffectiveWorkflowProfile()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_42",
                        number = 42,
                        title = "T",
                        stage = "backlog",
                        status = "active",
                        priority = "p2",
                        projectName = "demo",
                        workflowProfileId = "mohist/github-pr",
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "view", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var getReq = handler.Requests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("/api/projects/proj_abc/issues/42", getReq.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        Assert.Contains("profile:  mohist/github-pr", stdout);
    }

    [Fact]
    public async Task IssueShow_RendersInheritedDefaultWhenNoSelection()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_42",
                        number = 42,
                        title = "T",
                        stage = "backlog",
                        status = "active",
                        priority = "p2",
                        projectName = "demo",
                        workflowProfileId = "mohist/local",
                    },
                });
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "view", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("profile:  mohist/local", stdout);
    }

    [Fact]
    public async Task IssueShow_RoundTripsCreateThenShow()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        id = "issue_42",
                        number = 42,
                        title = "T",
                        workflowProfileId = "mohist/github-pr",
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
                        id = "issue_42",
                        number = 42,
                        title = "T",
                        stage = "backlog",
                        status = "active",
                        priority = "p2",
                        projectName = "demo",
                        workflowProfileId = "mohist/github-pr",
                    },
                });
            }
            return null!;
        });

        var createExit = await MohistCliCommands.RunAsync(
            http,
            ["issue", "create", "Round-trip", "--body", "Body content", "--workflow-profile", "mohist/github-pr"],
            output, error, fs, executor);
        Assert.Equal(0, createExit);

        var showExit = await MohistCliCommands.RunAsync(
            http, ["issue", "view", "42"], output, error, fs, executor);
        Assert.Equal(0, showExit);

        var stdout = output.ToString();
        Assert.Contains("profile:  mohist/github-pr", stdout);
    }

    [Fact]
    public async Task IssueUpdate_WithWorkflowProfileFlag_SendsWorkflowProfileIdInPatchBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "42", "--workflow-profile", "mohist/github-pr"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/projects/proj_abc/issues/42", patchReq.RequestUri?.PathAndQuery);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.Equal("mohist/github-pr", body["workflowProfileId"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_WithoutWorkflowProfileFlag_OmitsWorkflowProfileIdFromPatchBody()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "42", "--title", "Renamed"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("workflowProfileId"));
        Assert.Equal("Renamed", body["title"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_NoFlags_SendsPatchWithNoOptionalFieldKeys()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.False(body.AsObject().ContainsKey("workflowProfileId"));
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("body"));
        Assert.False(body.AsObject().ContainsKey("labels"));
        Assert.False(body.AsObject().ContainsKey("isDraft"));
    }

    [Fact]
    public async Task IssueUpdate_WorkflowProfileWithOtherFlags_PatchBodyOnlyContainsProvidedFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync();

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "42", "--workflow-profile", "mohist/github-pr", "--priority", "p1"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.Equal("mohist/github-pr", body["workflowProfileId"]?.GetValue<string>());
        Assert.Equal("p1", body["priority"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("title"));
        Assert.False(body.AsObject().ContainsKey("body"));
        Assert.False(body.AsObject().ContainsKey("labels"));
    }

    [Fact]
    public async Task IssueUpdate_WorkflowProfileRoundTripThroughShow_ReflectsNewValue()
    {
        var stored = new JsonObject
        {
            ["id"] = "issue_42",
            ["number"] = 42,
            ["title"] = "T",
            ["stage"] = "backlog",
            ["status"] = "active",
            ["priority"] = "p2",
            ["projectName"] = "demo",
            ["workflowProfileId"] = "mohist/local",
        };
        var lockObj = new object();

        var handler = new RecordingHttpHandler(async (req, ct) =>
        {
            lock (lockObj)
            {
                if (req.Method == HttpMethod.Get)
                {
                    return RecordingHttpHandler.Json(new
                    {
                        success = true,
                        data = stored.DeepClone(),
                    });
                }
            }

            if (req.Method == HttpMethod.Patch)
            {
                var bodyText = await req.Content!.ReadAsStringAsync(ct);
                var payload = JsonNode.Parse(bodyText)!;
                if (payload["workflowProfileId"] is JsonNode profileValue && profileValue is not null)
                {
                    lock (lockObj)
                    {
                        stored["workflowProfileId"] = profileValue.DeepClone();
                    }
                }
                return RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = stored.DeepClone(),
                });
            }
            return RecordingHttpHandler.Json(new { success = true, data = new { } });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fs = new FakeFileSystem();
        fs.AddFile(
            Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_abc\"}");

        var updateExit = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "42", "--workflow-profile", "mohist/github-pr"],
            output, error, fs, new FakeCommandExecutor());
        Assert.Equal(0, updateExit);

        var showExit = await MohistCliCommands.RunAsync(
            http, ["issue", "view", "42"], output, error, fs, new FakeCommandExecutor());
        Assert.Equal(0, showExit);

        var stdout = output.ToString();
        Assert.Contains("profile:  mohist/github-pr", stdout);
    }

    [Fact]
    public async Task IssueUpdate_WorkflowProfileOnStartedIssue_PrintsServerErrorAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                return RecordingHttpHandler.JsonError(
                    "Cannot change workflow profile on issue 42: workflow run wr_abc is active.",
                    code: "workflow_profile_locked",
                    statusCode: HttpStatusCode.Conflict);
            }
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["issue", "edit", "42", "--workflow-profile", "mohist/github-pr"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        var patchReq = handler.Requests.Last(r => r.Method == HttpMethod.Patch);
        var body = JsonNode.Parse(patchReq.Body!)!;
        Assert.Equal("mohist/github-pr", body["workflowProfileId"]?.GetValue<string>());
        var stderr = error.ToString();
        Assert.Contains("Cannot change workflow profile", stderr);
        Assert.Contains("workflow_profile_locked", stderr);
    }
}
