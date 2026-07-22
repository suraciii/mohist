using System.Net;
using System.Text;
using Mohist.Cli;
using Mohist.Cli.Tests.Compatibility;
using Xunit;

namespace Mohist.Cli.Tests.Project.Api;

public class ResolveProjectIdTests
{
    [Fact]
    public async Task ProjectOnly_ReturnsNonNullResolvedId()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync("mohist-local", null);

        Assert.NotNull(resolved);
        Assert.Equal("mohist-local", resolved);
        Assert.Equal("", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ProjectIdOnly_ReturnsNonNullResolvedId()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync(null, "proj_abc");

        Assert.NotNull(resolved);
        Assert.Equal("proj_abc", resolved);
        Assert.Equal("", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task MatchingProjectAndProjectId_ProceedsWithSingleValue()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync("mohist-local", "mohist-local");

        Assert.NotNull(resolved);
        Assert.Equal("mohist-local", resolved);
        Assert.Equal("", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ConflictingProjectAndProjectId_PrintsGuidedErrorAndDoesNotCallHttp()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync("mohist-local", "proj_other");

        Assert.Null(resolved);
        Assert.Contains("mohist-local", error.ToString());
        Assert.Contains("proj_other", error.ToString());
        Assert.Contains("--project", error.ToString());
        Assert.Contains("--project", error.ToString());
        Assert.Contains("Pass only one", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NoOptionsAndNoActiveProject_EmitsStandardizedDiagnostic()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync(null, null);

        Assert.Null(resolved);
        var err = error.ToString();
        Assert.Contains("mo project use", err);
        Assert.Contains("--project", err);
        Assert.Contains("name-or-id", err);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task BlankOptionsAndNoActiveProject_EmitsStandardizedDiagnostic()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync("", "");

        Assert.Null(resolved);
        var err = error.ToString();
        Assert.Contains("mo project use", err);
        Assert.Contains("--project", err);
        Assert.Contains("name-or-id", err);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ExplicitOptionIsNotOverriddenByActiveProject()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_active" }""");
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync("other-project", null);

        Assert.NotNull(resolved);
        Assert.Equal("other-project", resolved);
        Assert.Equal("", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NoOptionsWithActiveProject_FallsBackToActiveProject()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_active" }""");
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();
        var api = CreateApi(http, output, error, files);

        var resolved = await api.ResolveProjectIdAsync(null, null);

        Assert.NotNull(resolved);
        Assert.Equal("proj_active", resolved);
        Assert.Equal("", error.ToString());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task IssueShow_NoProjectAndNoActiveProject_ReturnsExitOneWithoutHttp()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "show", "83"],
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
    public async Task IssueShow_ProjectIdOptionIsNotOverriddenByActiveProject()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_active" }""");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 83, "title": "Test" } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "show", "83", "--project", "other-project"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(2, exitCode);
        Assert.Contains("--project-id is not supported", error.ToString());
        Assert.Empty(http.Requests);
    }

    private static MohistCliApi CreateApi(RecordingHttpHandler http, StringWriter output, StringWriter error, IFileSystem files) =>
        new(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
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
