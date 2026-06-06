using Mohist.Cli;
using Mohist.Server.Tests.Support;
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
        var statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_123" }""");
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
        Assert.Equal("/api/projects/proj_123/issues", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    private sealed class FakeFileSystem : Mohist.Server.Tests.Support.FakeFileSystem
    {
        public string SingleFileContents => Files.Values.Single();
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
