using System.Net;
using Mohist.Cli.Tests.Compatibility;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public class IssueCliFeedbackTests
{
    [Fact]
    public void Feedback_Help_ListsListAndShowSubcommands()
    {
        var help = RenderHelp(["issue", "feedback", "--help"]);

        Assert.Contains("list", help);
        Assert.Contains("show", help);
    }

    [Fact]
    public void FeedbackList_Help_ListsStageProjectAndOutputOptions()
    {
        var help = RenderHelp(["issue", "feedback", "list", "--help"]);

        Assert.Contains("--stage", help);
        Assert.Contains("--project", help);
        Assert.DoesNotContain("--project-id", help);
        Assert.Contains("--json", help);
    }

    [Fact]
    public void FeedbackShow_Help_ListsFeedbackLatestAndStageOptions()
    {
        var help = RenderHelp(["issue", "feedback", "show", "--help"]);

        Assert.Contains("--feedback", help);
        Assert.Contains("--latest", help);
        Assert.Contains("--stage", help);
    }

    [Fact]
    public async Task FeedbackList_Default_ResolvesProjectAndCallsFeedbackEndpoint()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task FeedbackList_ByProjectId_ResolvesAndCallsFeedbackEndpoint()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "proj_f6c141d63b6243bfbb481737b2243b87"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_f6c141d63b6243bfbb481737b2243b87/issues/42/feedback",
            http.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task FeedbackList_StageFilter_AppendsStageQuery()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "mohist-local", "--stage", "plan"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback?stage=plan",
            http.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task FeedbackList_OutputTable_RendersTableHeadersAndRows()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "fb_aaa", "stage": "plan", "status": "open", "createdAt": "2026-06-15T00:00:00Z", "body": "first feedback" },
                { "id": "fb_bbb", "stage": "build", "status": "resolved", "createdAt": "2026-06-15T01:00:00Z", "body": "second feedback" }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "mohist-local",],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("stage", text);
        Assert.Contains("status", text);
        Assert.Contains("createdAt", text);
        Assert.Contains("body", text);
        Assert.Contains("fb_aaa", text);
        Assert.Contains("plan", text);
        Assert.Contains("open", text);
        Assert.Contains("first feedback", text);
        Assert.Contains("fb_bbb", text);
        Assert.Contains("build", text);
        Assert.Contains("resolved", text);
        Assert.Contains("second feedback", text);
    }

    [Fact]
    public async Task FeedbackList_OutputJson_EmitsValidJsonArray()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "fb_aaa", "issueNumber": 42, "workflowRunId": "wr_x", "stage": "plan", "status": "open", "body": "feedback", "createdAt": "2026-06-15T00:00:00Z" }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "mohist-local", "--json", "id"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var parsed = JsonNode.Parse(output.ToString()) as JsonArray;
        Assert.NotNull(parsed);
        Assert.Single(parsed!);
        var first = parsed![0] as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("fb_aaa", first!["id"]?.GetValue<string>());
        Assert.Equal(42, first!["issueNumber"]?.GetValue<int>());
        Assert.Equal("plan", first!["stage"]?.GetValue<string>());
        Assert.Equal("open", first!["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task FeedbackShow_ByFeedbackId_CallsShowEndpoint()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": { "id": "fb_123", "issueNumber": 42, "stage": "plan", "status": "open", "body": "feedback" }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "show", "42", "--feedback", "fb_123", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback/fb_123",
            http.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task FeedbackShow_OutputJson_EmitsValidJsonObject()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "id": "fb_123",
                "issueNumber": 42,
                "workflowRunId": "wr_xyz",
                "stage": "plan",
                "status": "open",
                "body": "Please add error handling",
                "createdAt": "2026-06-15T00:00:00Z",
                "resolution": null
              }
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "show", "42", "--feedback", "fb_123", "--project", "mohist-local", "--json", "id"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var obj = JsonNode.Parse(output.ToString()) as JsonObject;
        Assert.NotNull(obj);
        Assert.Equal("fb_123", obj!["id"]?.GetValue<string>());
        Assert.Equal(42, obj!["issueNumber"]?.GetValue<int>());
        Assert.Equal("wr_xyz", obj!["workflowRunId"]?.GetValue<string>());
        Assert.Equal("plan", obj!["stage"]?.GetValue<string>());
        Assert.Equal("open", obj!["status"]?.GetValue<string>());
        Assert.Equal("Please add error handling", obj!["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task FeedbackShow_Latest_ListsFirstThenCallsShowForNewest()
    {
        var listJson = """
            {
              "success": true,
              "data": [
                { "id": "fb_latest", "stage": "plan", "status": "open", "createdAt": "2026-06-15T02:00:00Z" },
                { "id": "fb_old",    "stage": "plan", "status": "resolved", "createdAt": "2026-06-15T01:00:00Z" }
              ]
            }
            """;
        var showJson = """
            {
              "success": true,
              "data": { "id": "fb_latest", "issueNumber": 42, "stage": "plan", "status": "open" }
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, listJson);
        http.EnqueueJson(HttpStatusCode.OK, showJson);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "show", "42", "--latest", "--project", "mohist-local", "--json", "id"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback",
            http.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback/fb_latest",
            http.Requests[1].RequestUri!.PathAndQuery);
        var obj = JsonNode.Parse(output.ToString()) as JsonObject;
        Assert.NotNull(obj);
        Assert.Equal("fb_latest", obj!["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task FeedbackShow_LatestWithStageFilter_FiltersByStage()
    {
        var listJson = """
            {
              "success": true,
              "data": [
                { "id": "fb_latest_plan", "stage": "plan", "status": "open", "createdAt": "2026-06-15T02:00:00Z" },
                { "id": "fb_latest_build", "stage": "build", "status": "open", "createdAt": "2026-06-15T03:00:00Z" }
              ]
            }
            """;
        var showJson = """
            {
              "success": true,
              "data": { "id": "fb_latest_plan", "issueNumber": 42, "stage": "plan", "status": "open" }
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, listJson);
        http.EnqueueJson(HttpStatusCode.OK, showJson);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "show", "42", "--latest", "--stage", "plan", "--project", "mohist-local", "--json", "id"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback?stage=plan",
            http.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback/fb_latest_plan",
            http.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task FeedbackShow_NoFeedbackArgOrLatest_FailsBeforeHttpCall()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "show", "42", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--feedback", err);
        Assert.Contains("--latest", err);
    }

    [Fact]
    public async Task FeedbackList_ServerUnavailable_PrintsServerIsNotRunning()
    {
        var http = new ThrowingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = error.ToString();
        Assert.Contains("Server is not running", err);
        Assert.Contains("mo server start", err);
    }

    [Fact]
    public async Task FeedbackShow_ServerUnavailable_PrintsServerIsNotRunning()
    {
        var http = new ThrowingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "show", "42", "--feedback", "fb_123", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = error.ToString();
        Assert.Contains("Server is not running", err);
        Assert.Contains("mo server start", err);
    }

    [Fact]
    public async Task FeedbackList_NoActiveProject_PrintsGuidedDiagnostic()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("mo project use", err);
        Assert.Contains("--project", err);
    }

    [Fact]
    public async Task FeedbackList_OutputJsonAndTable_SendSameHttpRequest()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "fb_aaa", "stage": "plan", "status": "open", "createdAt": "2026-06-15T00:00:00Z", "body": "feedback" }
              ]
            }
            """;

        var jsonHttp = new RecordingHttpHandler();
        jsonHttp.EnqueueJson(HttpStatusCode.OK, json);
        await MohistCliCommands.RunAsync(
            new HttpClient(jsonHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "mohist-local", "--json", "id"],
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        var tableHttp = new RecordingHttpHandler();
        tableHttp.EnqueueJson(HttpStatusCode.OK, json);
        await MohistCliCommands.RunAsync(
            new HttpClient(tableHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "feedback", "list", "42", "--project", "mohist-local",],
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        var jsonReq = jsonHttp.Requests.Single();
        var tableReq = tableHttp.Requests.Single();
        Assert.Equal(HttpMethod.Get, jsonReq.Method);
        Assert.Equal(HttpMethod.Get, tableReq.Method);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback", jsonReq.RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects/mohist-local/issues/42/feedback", tableReq.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task RenderTable_FeedbackList_ContainsHeadersAndTruncatesLongBody()
    {
        var longBody = new string('B', 200);
        var data = JsonNode.Parse($$"""
            [
              { "id": "fb_aaa", "stage": "plan", "status": "open", "createdAt": "2026-06-15T00:00:00Z", "body": "{{longBody}}" }
            ]
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.FeedbackList);

        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("stage", text);
        Assert.Contains("status", text);
        Assert.Contains("createdAt", text);
        Assert.Contains("body", text);
        Assert.Contains("fb_aaa", text);
        Assert.Contains("…", text);
        Assert.DoesNotContain(longBody, text);
    }

    [Fact]
    public async Task RenderTable_FeedbackShow_ContainsKeyFieldsAndBody()
    {
        var data = JsonNode.Parse("""
            {
              "id": "fb_123",
              "issueNumber": 42,
              "workflowRunId": "wr_xyz",
              "stage": "plan",
              "status": "open",
              "body": "Please add error handling",
              "createdAt": "2026-06-15T00:00:00Z",
              "resolution": null
            }
            """);

        var output = new StringWriter();
        var api = new MohistCliApi(
            RejectingHttpMessageHandler.CreateClient(),
            output,
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        await api.RenderTableAsync(data, MohistCliApi.TableShape.FeedbackShow);

        var text = output.ToString();
        Assert.Contains("id:", text);
        Assert.Contains("fb_123", text);
        Assert.Contains("issue:", text);
        Assert.Contains("workflow run:", text);
        Assert.Contains("wr_xyz", text);
        Assert.Contains("stage:", text);
        Assert.Contains("plan", text);
        Assert.Contains("status:", text);
        Assert.Contains("open", text);
        Assert.Contains("body:", text);
        Assert.Contains("Please add error handling", text);
    }

    private static string RenderHelp(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(RejectingHttpMessageHandler.CreateClient(), TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(new FakeFileSystem());
        services.AddSingleton<ICommandExecutor>(new NoopCommandExecutor());
        services.AddSingleton<IServiceInstaller>(sp => new SystemdServiceInstaller(TextWriter.Null, TextWriter.Null, new FakeFileSystem(), sp.GetRequiredService<ICommandExecutor>()));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();
        services.AddSingleton<InfoCollector>();

        var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<MohistCliApi>();
        var root = MohistCliCommands.Build(api, provider);

        using var writer = new StringWriter();
        var config = new System.CommandLine.InvocationConfiguration { Output = writer, Error = writer };
        root.Parse(args).Invoke(config);
        return writer.ToString();
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

        public void EnqueueJson(HttpStatusCode status, string json)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "success": true, "data": null }""", Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }
}
