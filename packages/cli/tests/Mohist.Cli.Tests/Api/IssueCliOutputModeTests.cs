using System.Net;
using Mohist.Cli.Tests.Compatibility;
using System.Text;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public class IssueCliOutputModeTests
{
    [Fact]
    public void ValidateOutputMode_Null_ReturnsTable()
    {
        var result = MohistCliApi.ValidateOutputMode(null);

        var valid = Assert.IsType<MohistCliApi.OutputModeResult.Valid>(result);
        Assert.Equal("table", valid.Mode);
    }

    [Fact]
    public void ValidateOutputMode_Json_ReturnsDiscovery()
    {
        var result = MohistCliApi.ValidateOutputMode("json");

        var valid = Assert.IsType<MohistCliApi.OutputModeResult.Valid>(result);
        Assert.Equal("discover", valid.Mode);
    }

    [Fact]
    public void ValidateOutputMode_Table_ReturnsTable()
    {
        var result = MohistCliApi.ValidateOutputMode("table");

        var valid = Assert.IsType<MohistCliApi.OutputModeResult.Valid>(result);
        Assert.Equal("table", valid.Mode);
    }

    [Fact]
    public void ValidateOutputMode_FieldSelection_ReturnsJsonSelection()
    {
        var result = MohistCliApi.ValidateOutputMode("yaml");

        var valid = Assert.IsType<MohistCliApi.OutputModeResult.Valid>(result);
        Assert.Equal("json:yaml", valid.Mode);
    }

    [Fact]
    public void ValidateOutputMode_DoesNotTouchHttp()
    {
        var http = new RecordingHttpHandler();

        _ = MohistCliApi.ValidateOutputMode("yaml");
        _ = MohistCliApi.ValidateOutputMode("json");
        _ = MohistCliApi.ValidateOutputMode("table");
        _ = MohistCliApi.ValidateOutputMode(null);

        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task PrintWithOutputAsync_Json_PrintsSameDataPayloadAsPrintResponseAsync()
    {
        const string json = """
            {
              "success": true,
              "data": { "id": "proj_1", "name": "mohist-local" }
            }
            """;

        var baselineApi = BuildApi(BuildHandler(json));
        var baselineOutput = new StringWriter();
        var baselineError = new StringWriter();
        var baselineExit = await baselineApi.PrintGetAsync("/api/projects");

        var api = BuildApi(BuildHandler(json));
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await api.PrintWithOutputAsync("/api/projects", "json");

        Assert.Equal(baselineExit, exit);
        Assert.Equal(baselineOutput.ToString(), output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task PrintWithOutputAsync_Json_KeepsChineseCharacters_Unescaped()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "id": "issue_1",
                "number": 168,
                "title": "[Dashboard] 视图",
                "body": "中文内容测试"
              }
            }
            """;

        var output = new StringWriter();
        var api = BuildApiWithOutput(BuildHandler(json), output, new StringWriter());

        await api.PrintWithOutputAsync("/api/projects/proj_1/issues/168", "json", "IssueShow");

        var result = output.ToString();
        Assert.Contains("视图", result);
        Assert.Contains("中文内容测试", result);
        Assert.DoesNotContain("\\u", result);
    }

    [Fact]
    public async Task PrintWithOutputAsync_JsonAndTable_SendSameHttpRequest()
    {
        var jsonHandler = BuildHandler("""
            { "success": true, "data": [{ "id": "proj_1", "name": "mohist-local" }] }
            """);
        var tableHandler = BuildHandler("""
            { "success": true, "data": [{ "id": "proj_1", "name": "mohist-local" }] }
            """);

        var jsonApi = BuildApi(jsonHandler);
        await jsonApi.PrintWithOutputAsync("/api/projects", "json");

        var tableApi = BuildApi(tableHandler);
        await tableApi.PrintWithOutputAsync("/api/projects", "table", "ProjectList");

        Assert.Single(jsonHandler.Requests);
        Assert.Single(tableHandler.Requests);

        var jsonReq = jsonHandler.Requests.Single();
        var tableReq = tableHandler.Requests.Single();

        Assert.Equal(HttpMethod.Get, jsonReq.Method);
        Assert.Equal(HttpMethod.Get, tableReq.Method);
        Assert.Equal("/api/projects", jsonReq.RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects", tableReq.RequestUri!.PathAndQuery);
    }

    private static RecordingHttpHandler BuildHandler(string json) =>
        RecordingHttpHandler.WithQueuedJson(HttpStatusCode.OK, json);

    private static MohistCliApi BuildApi(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

    private static MohistCliApi BuildApiWithOutput(HttpMessageHandler handler, StringWriter output, StringWriter error) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public static RecordingHttpHandler WithQueuedJson(HttpStatusCode status, string json)
        {
            var handler = new RecordingHttpHandler();
            handler.EnqueueJson(status, json);
            return handler;
        }

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
}
