using System.Net;
using Mohist.Server.UnitTests.Support;
using System.Text;
using System.Text.Json.Nodes;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public class IssueCliStartReadinessTests
{
    [Fact]
    public async Task IssueCreate_NoDraftFlag_SendsIsDraftTrueByDefault()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 1, "title": "Default draft", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Default draft", "--body", "content", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues", req.RequestUri!.PathAndQuery);
        var body = http.ReadCapturedBody(req);
        Assert.True(body?["isDraft"]?.GetValue<bool>());
        Assert.Equal("Default draft", body?["title"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_ReadyFlag_SendsIsDraftFalse()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 1, "title": "Ready", "isDraft": false, "canStart": true, "blocker": null }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Ready", "--body", "content", "--project", "mohist-local", "--ready"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.False(body?["isDraft"]?.GetValue<bool>());
    }

    [Fact]
    public async Task IssueCreate_DraftFlag_SendsIsDraftTrue()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 1, "title": "Draft", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Draft", "--body", "content", "--project", "mohist-local", "--draft"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.True(body?["isDraft"]?.GetValue<bool>());
    }

    [Fact]
    public async Task IssueCreate_BothReadyAndDraft_FailsWithErrorAndNoHttpCall()
    {
        var http = new RecordingHttpHandler();

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Conflicting", "--body", "content", "--project", "mohist-local", "--ready", "--draft"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--ready", err);
        Assert.Contains("--draft", err);
        Assert.Contains("mutually exclusive", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueCreate_DraftResponse_PrintsMarkReadyGuidance_NoStartTip()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 83, "title": "Half-baked", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Half-baked", "--body", "content", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("mo issue update 83 --ready", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mo issue start 83", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before starting", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueCreate_DraftResponse_PrintsGuidanceAfterIssueData()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 83, "title": "Half-baked", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Half-baked", "--body", "content", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        var dataIndex = text.IndexOf("\"number\"", StringComparison.Ordinal);
        var guidanceIndex = text.IndexOf("mo issue update", StringComparison.OrdinalIgnoreCase);
        Assert.True(dataIndex >= 0, "expected issue data in output");
        Assert.True(guidanceIndex >= 0, "expected guidance in output");
        Assert.True(guidanceIndex > dataIndex, "expected guidance to be printed after the issue data");
    }

    [Fact]
    public async Task IssueCreate_ReadyStartableResponse_PrintsStartTip()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 84, "title": "Pickable", "isDraft": false, "canStart": true, "blocker": null }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Pickable", "--body", "content", "--project", "mohist-local", "--ready"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("mo issue start 84", text);
        Assert.DoesNotContain("mo issue update", text);
    }

    [Fact]
    public async Task IssueCreate_ReadyButWaitingForPrerequisite_DoesNotPrintStartTipAndPrintsWaitingReason()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": {
                "id": "issue_1", "number": 85, "title": "Dependent",
                "isDraft": false, "canStart": false,
                "blocker": { "kind": "waiting-for", "issue": { "number": 42, "title": "Blocker" } }
              }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Dependent", "--body", "content", "--project", "mohist-local", "--ready"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Waiting for #42", text);
        Assert.DoesNotContain("mo issue start 85", text);
    }

    [Fact]
    public async Task IssueUpdate_ReadyFlag_SendsIsDraftFalse()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 83, "title": "Marked ready", "isDraft": false, "canStart": true, "blocker": null }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "update", "83", "--ready", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(new HttpMethod("PATCH"), req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/83", req.RequestUri!.PathAndQuery);
        var body = http.ReadCapturedBody(req);
        Assert.False(body?["isDraft"]?.GetValue<bool>());
    }

    [Fact]
    public async Task IssueUpdate_DraftFlag_SendsIsDraftTrue()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": { "id": "issue_1", "number": 83, "title": "Returned to draft", "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "update", "83", "--draft", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.True(body?["isDraft"]?.GetValue<bool>());
    }

    [Fact]
    public async Task IssueUpdate_ReadyAndDraft_FailsWithErrorAndNoHttpCall()
    {
        var http = new RecordingHttpHandler();

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "update", "83", "--ready", "--draft", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--ready", err);
        Assert.Contains("--draft", err);
        Assert.Contains("mutually exclusive", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueUpdate_NoDraftFlag_DoesNotSendIsDraftField()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": { "id": "issue_1", "number": 83, "title": "Renamed" } }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "update", "83", "--title", "Renamed", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("Renamed", body?["title"]?.GetValue<string>());
        Assert.Null(body?["isDraft"]);
    }

    [Fact]
    public async Task IssueUpdate_ReadyFlag_DoesNotStartIssue()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": { "id": "issue_1", "number": 83, "title": "Ready", "isDraft": false } }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "update", "83", "--ready", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(new HttpMethod("PATCH"), req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/83", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueList_Table_RendersDraftAndWaitingStatesFromApiFields()
    {
        var data = JsonNode.Parse("""
            [
              { "number": 1, "title": "draft-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2",
                "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } },
              { "number": 2, "title": "ready-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2",
                "isDraft": false, "canStart": true, "blocker": null },
              { "number": 3, "title": "waiting-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2",
                "isDraft": false, "canStart": false,
                "blocker": { "kind": "waiting-for", "issue": { "number": 99, "title": "Blocker" } } }
            ]
            """);
        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueList);

        var text = output.ToString();
        Assert.Contains("draft", text);
        Assert.Contains("ready", text);
        Assert.Contains("Waiting for #99", text);
    }

    [Fact]
    public async Task IssueList_Table_DoesNotReferenceStartEligibilityOrWaitingForDeliveryFields()
    {
        var data = JsonNode.Parse("""
            [
              { "number": 1, "title": "draft-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2",
                "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            ]
            """);
        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueList);

        var text = output.ToString();
        Assert.DoesNotContain("startEligibility", text);
        Assert.DoesNotContain("waitingForDelivery", text);
        Assert.DoesNotContain("Reason", text);
    }

    [Fact]
    public async Task IssueShow_Table_RendersDraftStateFromIsDraft()
    {
        var data = JsonNode.Parse("""
            {
              "number": 1, "title": "draft-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2",
              "isDraft": true, "canStart": false, "blocker": { "kind": "draft" }
            }
            """);
        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow);

        var text = output.ToString();
        Assert.Contains("state:", text);
        Assert.Contains("draft", text);
    }

    [Fact]
    public async Task IssueShow_Table_RendersWaitingReasonFromBlocker()
    {
        var data = JsonNode.Parse("""
            {
              "number": 1, "title": "waiting-issue", "workflowStage": "plan", "status": "backlog", "priority": "p2",
              "isDraft": false, "canStart": false,
              "blocker": { "kind": "waiting-for", "issue": { "number": 200, "title": "Blocker" } }
            }
            """);
        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow);

        var text = output.ToString();
        Assert.Contains("Waiting for #200", text);
        Assert.DoesNotContain("startEligibility", text);
        Assert.DoesNotContain("waitingForDelivery", text);
    }

    [Fact]
    public async Task IssueStart_DraftRejection_SurfacesServerMessageAndExitsNonZero()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.BadRequest, """
            {
              "success": false,
              "error": "Issue #201 is still a draft and cannot be started",
              "code": "draft",
              "details": { "canStart": false, "blocker": { "kind": "draft" } }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "start", "201", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = error.ToString();
        Assert.Contains("still a draft", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("draft", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueStart_WaitingForPrerequisiteRejection_SurfacesServerMessageAndExitsNonZero()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.BadRequest, """
            {
              "success": false,
              "error": "Issue #201 is waiting for prerequisite issue #200",
              "code": "waiting_for_prerequisite",
              "details": { "canStart": false, "blocker": { "kind": "waiting-for", "issue": { "number": 200 } } }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "start", "201", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = error.ToString();
        Assert.Contains("waiting for prerequisite issue #200", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueStart_SendsSingleStartRequest()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.BadRequest, """
            {
              "success": false,
              "error": "Issue #201 is still a draft and cannot be started",
              "code": "draft"
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "start", "201", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Single(http.Requests);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/201/start", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public void ResolveDraftFlagState_HandlesAllCombinations()
    {
        Assert.Equal(MohistCliCommands.DraftFlagState.Conflicting, MohistCliCommands.ResolveDraftFlagState(true, true));
        Assert.Equal(MohistCliCommands.DraftFlagState.Ready, MohistCliCommands.ResolveDraftFlagState(true, false));
        Assert.Equal(MohistCliCommands.DraftFlagState.Draft, MohistCliCommands.ResolveDraftFlagState(false, true));
        Assert.Equal(MohistCliCommands.DraftFlagState.Unspecified, MohistCliCommands.ResolveDraftFlagState(false, false));
    }

    [Fact]
    public void FormatIssueState_ReadsFromApiFieldsNotBody()
    {
        var data = JsonNode.Parse("""
            { "isDraft": true, "canStart": false, "blocker": { "kind": "draft" } }
            """);
        Assert.Equal("draft", TableRenderer.FormatIssueState(data));

        var ready = JsonNode.Parse("""
            { "isDraft": false, "canStart": true, "blocker": null }
            """);
        Assert.Equal("ready", TableRenderer.FormatIssueState(ready));

        var waiting = JsonNode.Parse("""
            { "isDraft": false, "canStart": false, "blocker": { "kind": "waiting-for", "issue": { "number": 77 } } }
            """);
        Assert.Equal("Waiting for #77", TableRenderer.FormatIssueState(waiting));
    }

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public Dictionary<HttpRequestMessage, string> CapturedBodies { get; } = new();

        public void EnqueueJson(HttpStatusCode status, string json)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        public JsonNode? ReadCapturedBody(HttpRequestMessage request)
        {
            if (CapturedBodies.TryGetValue(request, out var body))
                return JsonNode.Parse(body);
            return null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                CapturedBodies[request] = body;
            }
            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "success": true, "data": null }""", Encoding.UTF8, "application/json"),
                };
        }
    }
}
