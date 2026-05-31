using Mohist.Cli;
using System.Net;
using System.Text;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ProjectCliSpecs
{
    [Fact]
    public async Task ProjectUse_ByName_PersistsActiveProjectInCliState()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": { "id": "proj_123", "name": "e2e-smoke" }
            }
            """);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "use", "e2e-smoke"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Post, http.Requests.Single().Method);
        Assert.Equal("/api/projects/e2e-smoke/use", http.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Contains("\"activeProjectId\": \"proj_123\"", files.SingleFileContents);
        Assert.Contains("Active project: e2e-smoke (proj_123)", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Use_ByName_PersistsActiveProjectInCliState()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": { "id": "proj_456", "name": "mohist" }
            }
            """);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["use", "mohist"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Post, http.Requests.Single().Method);
        Assert.Equal("/api/projects/mohist/use", http.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Contains("\"activeProjectId\": \"proj_456\"", files.SingleFileContents);
        Assert.Contains("Active project: mohist (proj_456)", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task IssueList_UsesPersistedActiveProjectWhenProjectIdIsOmitted()
    {
        var files = new FakeFileSystem();
        await files.WriteAllTextAsync("/state/cli-state.json", """{ "activeProjectId": "proj_123" }""");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": [] }""");

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list"],
            new StringWriter(),
            new StringWriter(),
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/issues?projectId=proj_123", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public string SingleFileContents => _files.Values.Single();

        public Task WriteAllTextAsync(string path, string contents)
        {
            _files[Path.GetFullPath(path)] = contents;
            return Task.CompletedTask;
        }

        public Task<string> ReadAllTextAsync(string path)
        {
            return Task.FromResult(SingleFileContents);
        }

        public bool Exists(string path) => _files.Count > 0;

        public void Delete(string path) => _files.Clear();
    }

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

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
