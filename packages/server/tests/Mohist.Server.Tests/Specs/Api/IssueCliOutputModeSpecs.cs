using System.Net;
using System.Text;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Api;

public class IssueCliOutputModeSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ValidateOutputMode_Null_ReturnsJson()
    {
        var result = MohistCliApi.ValidateOutputMode(null);

        var valid = Assert.IsType<MohistCliApi.OutputModeResult.Valid>(result);
        Assert.Equal("json", valid.Mode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ValidateOutputMode_Json_ReturnsJson()
    {
        var result = MohistCliApi.ValidateOutputMode("json");

        var valid = Assert.IsType<MohistCliApi.OutputModeResult.Valid>(result);
        Assert.Equal("json", valid.Mode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ValidateOutputMode_Table_ReturnsTable()
    {
        var result = MohistCliApi.ValidateOutputMode("table");

        var valid = Assert.IsType<MohistCliApi.OutputModeResult.Valid>(result);
        Assert.Equal("table", valid.Mode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ValidateOutputMode_Unknown_ReturnsFailureThatListsAcceptedValues()
    {
        var result = MohistCliApi.ValidateOutputMode("yaml");

        var invalid = Assert.IsType<MohistCliApi.OutputModeResult.Invalid>(result);
        Assert.Contains("table", invalid.Message);
        Assert.Contains("json", invalid.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ActionDelegate_UnknownOutputMode_DoesNotMakeHttpCall()
    {
        var http = BuildHandler("""
            { "success": true, "data": [] }
            """);
        var api = BuildApi(http);
        var output = new StringWriter();
        var error = new StringWriter();

        var validation = MohistCliApi.ValidateOutputMode("yaml");
        Assert.IsType<MohistCliApi.OutputModeResult.Invalid>(validation);

        Assert.Empty(http.Requests);

        await error.WriteLineAsync(Assert.IsType<MohistCliApi.OutputModeResult.Invalid>(validation).Message);

        Assert.Empty(http.Requests);
        Assert.Contains("table", error.ToString());
        Assert.Contains("json", error.ToString());
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
