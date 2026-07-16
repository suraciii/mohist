using Mohist.Cli;
using Mohist.Server.UnitTests.Support;
using System.Net;
using System.Text;
using Xunit;

namespace Mohist.Server.UnitTests.Project.Api;

public class ProjectCliTests
{
    [Fact]
    public async Task ProjectUse_ByName_PersistsActiveProjectInCliState()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": { "id": "proj_123", "name": "spec-smoke" }
            }
            """);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "use", "spec-smoke"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Post, http.Requests.Single().Method);
        Assert.Equal("/api/projects/spec-smoke/use", http.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Contains("\"activeProjectId\": \"proj_123\"", files.SingleFileContents);
        Assert.Contains("Active project: spec-smoke (proj_123)", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task ProjectCreate_NameOnly_SendsNameOnlyBodyAndOmitsPathFields()
    {
        var files = new FakeFileSystem();
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "id": "proj_new", "name": "alpha" }
            }
            """);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "create", "alpha"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects", request.RequestUri!.PathAndQuery);
        var body = http.RequestBodies.Single();
        Assert.Contains("\"name\": \"alpha\"", body);
        Assert.DoesNotContain("\"path\"", body);
        Assert.DoesNotContain("\"effectivePath\"", body);
        Assert.DoesNotContain("\"baseBranch\"", body);
    }

    [Fact]
    public async Task ProjectList_DisplaysNamesWithCurrentMarkerAndOmitsPathFields()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_b" }""");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": [
                { "id": "proj_a", "name": "alpha" },
                { "id": "proj_b", "name": "beta" },
                { "id": "proj_c", "name": "gamma" }
              ]
            }
            """);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list", "--output", "table"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects", http.Requests.Single().RequestUri!.PathAndQuery);
        var lines = output.ToString().TrimEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("  alpha", lines);
        Assert.Contains("* beta", lines);
        Assert.Contains("  gamma", lines);
        Assert.DoesNotContain("path", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task RepositoryAdd_WithGitUrl_SendsGitUrlBaseBranchNameAndIsDefault()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_123" }""");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": {
                "id": "proj_123",
                "name": "spec",
                "repositories": [
                  { "name": "backend", "gitUrl": "git@example.com:backend.git", "baseBranch": "main", "isDefault": true }
                ]
              }
            }
            """);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["repository", "add", "backend", "--git-url", "git@example.com:backend.git", "--base-branch", "main", "--set-default"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/proj_123/repositories", request.RequestUri!.PathAndQuery);
        var body = http.RequestBodies.Single();
        Assert.Contains("\"name\": \"backend\"", body);
        Assert.Contains("\"gitUrl\": \"git@example.com:backend.git\"", body);
        Assert.Contains("\"baseBranch\": \"main\"", body);
        Assert.Contains("\"isDefault\": true", body);
        Assert.DoesNotContain("\"path\"", body);
        Assert.DoesNotContain("\"remote\"", body);
        Assert.DoesNotContain("\"resolvedPath\"", body);
    }

    [Fact]
    public async Task RepositoryAdd_WithoutGitUrl_IsRejectedBeforeApiCall()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_123" }""");
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["repository", "add", "backend"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.NotEqual(0, exitCode);
        Assert.Empty(http.Requests);
        Assert.Contains("git-url", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepositoryAdd_ServerValidationError_IsSurfaced()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
            ".mohist",
            "cli-state.json");
        files.AddDirectory(Path.GetDirectoryName(statePath)!);
        await files.WriteAllTextAsync(statePath, """{ "activeProjectId": "proj_123" }""");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.BadRequest, """
            {
              "success": false,
              "error": "gitUrl is required",
              "code": "repository_giturl_required"
            }
            """);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["repository", "add", "backend", "--git-url", "/proj/backend"],
            output,
            error,
            files,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.NotEqual(0, exitCode);
        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        var body = http.RequestBodies.Single();
        Assert.Contains("\"gitUrl\": \"/proj/backend\"", body);
        Assert.DoesNotContain("\"path\"", body);
        Assert.Contains("gitUrl is required", error.ToString());
    }

    [Fact]
    public async Task IssueList_UsesPersistedActiveProjectWhenProjectIdIsOmitted()
    {
        var files = new FakeFileSystem();
        var statePath = Path.Combine(
            "/mohist-tests/user",
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
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_123/issues", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    private sealed class FakeFileSystem : Mohist.Server.UnitTests.Support.FakeFileSystem
    {
        public string SingleFileContents => Files.Values.Single();
    }

    private sealed class NoopCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((0, "", ""));
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        public void EnqueueJson(HttpStatusCode status, string json)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                RequestBodies.Add(body);
            }
            else
            {
                RequestBodies.Add("");
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
