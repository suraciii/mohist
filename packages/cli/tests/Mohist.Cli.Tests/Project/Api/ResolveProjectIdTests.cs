using System.Net;
using System.Text;
using Mohist.Cli;
using Mohist.Cli.Tests.Compatibility;
using Xunit;

namespace Mohist.Cli.Tests.Project.Api;

public sealed class ResolveProjectIdTests
{
    [Fact]
    public async Task ExplicitProject_ReturnsThatReference()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var api = CreateApi(http, new StringWriter(), new StringWriter(), files);

        var resolved = await api.ResolveProjectIdAsync("mohist-local");

        Assert.Equal("mohist-local", resolved);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ActiveProjectIsUsedWhenExplicitProjectIsMissing()
    {
        var files = new FakeFileSystem();
        files.AddFile(
            "/mohist-tests/user/.mohist/cli-state.json",
            "{\"activeProjectId\":\"proj_active\"}");
        var http = new RecordingHttpHandler();
        var api = CreateApi(http, new StringWriter(), new StringWriter(), files);

        var resolved = await api.ResolveProjectIdAsync(null);

        Assert.Equal("proj_active", resolved);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task MissingProjectAndStateReturnsNull()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var error = new StringWriter();
        var api = CreateApi(http, new StringWriter(), error, files);

        var resolved = await api.ResolveProjectIdAsync(null);

        Assert.Null(resolved);
        Assert.Contains("mo project use", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NoProjectAndNoActiveProject_ReturnsUsageFailureWithoutHttp()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "view", "83"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(1, exitCode);
        Assert.Contains("mo project use", error.ToString());
        Assert.Contains("--project", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ExplicitProjectIsNotOverriddenByActiveProject()
    {
        var files = new FakeFileSystem();
        var statePath = "/mohist-tests/user/.mohist/cli-state.json";
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, "{ \"activeProjectId\": \"proj_active\" }");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, "{ \"success\": true, \"data\": { \"id\": \"issue_1\", \"number\": 83, \"title\": \"Test\" } }");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "view", "83", "--project", "other-project"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal("/api/projects/other-project/issues/83", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    private static MohistCliApi CreateApi(
        RecordingHttpHandler http,
        StringWriter output,
        StringWriter error,
        IFileSystem files) =>
        new(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName,
            string[] args,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode status, string json) =>
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"success\":true,\"data\":null}", Encoding.UTF8, "application/json"),
                });
        }
    }
}
