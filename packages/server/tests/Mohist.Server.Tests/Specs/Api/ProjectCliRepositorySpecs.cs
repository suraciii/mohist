using System.CommandLine;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Api;

public class ProjectCliRepositorySpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoHelp_ListsListAddSetDefaultRemoveAndDocumentsProjectOption()
    {
        var help = RenderHelp(["project", "repo", "--help"]);

        Assert.Contains("list", help);
        Assert.Contains("add", help);
        Assert.Contains("set-default", help);
        Assert.Contains("remove", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoListHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["project", "repo", "list", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoAddHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["project", "repo", "add", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoSetDefaultHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["project", "repo", "set-default", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoRemoveHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["project", "repo", "remove", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectHelp_ListsRepoAlongsideExistingSubcommands()
    {
        var help = RenderHelp(["project", "--help"]);

        Assert.Contains("list", help);
        Assert.Contains("create", help);
        Assert.Contains("show", help);
        Assert.Contains("use", help);
        Assert.Contains("delete", help);
        Assert.Contains("repo", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoListHelp_ListsOutputOption()
    {
        var help = RenderHelp(["project", "repo", "list", "--help"]);

        Assert.Contains("--output", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoAddHelp_DoesNotAdvertiseOutputOption()
    {
        var help = RenderHelp(["project", "repo", "add", "--help"]);

        Assert.DoesNotContain("--output", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoSetDefaultHelp_DoesNotAdvertiseOutputOption()
    {
        var help = RenderHelp(["project", "repo", "set-default", "--help"]);

        Assert.DoesNotContain("--output", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public void ProjectRepoRemoveHelp_DoesNotAdvertiseOutputOption()
    {
        var help = RenderHelp(["project", "repo", "remove", "--help"]);

        Assert.DoesNotContain("--output", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoList_ByProjectName_SendsGetOnResolvedPath()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": [
                { "name": "main", "path": "/repo/main", "remote": null, "baseBranch": "main", "isDefault": true }
              ]
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "list", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects/mohist-local/repositories", req.RequestUri!.PathAndQuery);
        Assert.Equal("", error.ToString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoList_ByProjectId_SendsGetOnResolvedPath()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "list", "--project-id", "proj_f6c141d63b6243bfbb481737b2243b87"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_f6c141d63b6243bfbb481737b2243b87/repositories", http.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Equal("", error.ToString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoList_NoActiveProjectAndNoOption_PrintsGuidedDiagnostic()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "list"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("mo project use", err);
        Assert.Contains("--project", err);
        Assert.Contains("name-or-id", err);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoList_OutputTable_RendersRepoListTable()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "name": "main", "path": "/repo/main", "remote": null, "baseBranch": "main", "isDefault": true }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "list", "--project", "mohist-local", "--output", "table"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var text = output.ToString();
        Assert.Contains("name", text);
        Assert.Contains("path", text);
        Assert.Contains("remote", text);
        Assert.Contains("base branch", text);
        Assert.Contains("default", text);
        Assert.Contains("main", text);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoAdd_ByProjectName_SendsPostWithPayload()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "name": "api", "path": "/path", "remote": null, "baseBranch": "main", "isDefault": true }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "add", "--project", "mohist-local", "--name", "api", "--path", "/path", "--set-default"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/mohist-local/repositories", req.RequestUri!.PathAndQuery);

        var body = http.ReadCapturedBody(req);
        Assert.NotNull(body);
        Assert.Equal("api", body!["name"]?.GetValue<string>());
        Assert.Equal("/path", body["path"]?.GetValue<string>());
        Assert.True(body["setDefault"]?.GetValue<bool>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoAdd_ByProjectId_SendsPostWithPayload()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """
            {
              "success": true,
              "data": { "name": "api", "path": "/path", "remote": null, "baseBranch": "main", "isDefault": true }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "add", "--project-id", "proj_f6c141d63b6243bfbb481737b2243b87", "--name", "api", "--path", "/path", "--set-default"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Post, http.Requests.Single().Method);
        Assert.Equal("/api/projects/proj_f6c141d63b6243bfbb481737b2243b87/repositories", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoAdd_MissingName_WritesErrorAndExitsNonZeroWithNoHttpCall()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "add", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        Assert.Contains("--name", error.ToString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoSetDefault_SendsPatchWithSetDefaultTrue()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            {
              "success": true,
              "data": { "name": "api", "path": null, "remote": null, "baseBranch": "main", "isDefault": true }
            }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "set-default", "--project", "mohist-local", "api"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal("/api/projects/mohist-local/repositories/api", req.RequestUri!.PathAndQuery);

        var body = http.ReadCapturedBody(req);
        Assert.NotNull(body);
        Assert.True(body!["setDefault"]?.GetValue<bool>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoRemove_SendsDeleteOnResolvedPath()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": null }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "remove", "--project", "mohist-local", "api"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("/api/projects/mohist-local/repositories/api", req.RequestUri!.PathAndQuery);
        Assert.Equal("", error.ToString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoRemove_RmAlias_SendsDeleteOnResolvedPath()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": null }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "rm", "--project", "mohist-local", "api"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Delete, http.Requests.Single().Method);
        Assert.Equal("/api/projects/mohist-local/repositories/api", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoAdd_ConflictResponse_IsSurfacedAsNonZeroExit()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Conflict, """
            { "success": false, "error": "Repository 'api' already exists" }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "add", "--project", "mohist-local", "--name", "api", "--path", "/path"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Contains("api", error.ToString());
        Assert.Contains("already exists", error.ToString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ProjectRepoRemove_MissingRepositoryResponse_IsSurfacedAsNonZeroExit()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.NotFound, """
            { "success": false, "error": "Project or repository not found" }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "repo", "remove", "--project", "mohist-local", "missing-repo"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(4, exitCode);
        var err = error.ToString();
        Assert.Contains("not found", err, StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderHelp(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(new HttpClient(), TextWriter.Null, TextWriter.Null, RealFileSystem.Instance, new SystemCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(RealFileSystem.Instance);
        services.AddSingleton<ICommandExecutor>(new SystemCommandExecutor());
        services.AddSingleton<IServiceInstaller>(new SystemdServiceInstaller(TextWriter.Null, TextWriter.Null, RealFileSystem.Instance, new SystemCommandExecutor()));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();
        services.AddSingleton<InfoCollector>();
        services.AddSingleton<InfoRenderer>();

        var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<MohistCliApi>();
        var root = MohistCliCommands.Build(api, provider);

        using var writer = new StringWriter();
        var config = new InvocationConfiguration { Output = writer, Error = writer };
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
        public Dictionary<HttpRequestMessage, string> CapturedBodies { get; } = new();

        public void EnqueueJson(HttpStatusCode status, string json)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        public System.Text.Json.Nodes.JsonNode? ReadCapturedBody(HttpRequestMessage request)
        {
            if (CapturedBodies.TryGetValue(request, out var body))
                return System.Text.Json.Nodes.JsonNode.Parse(body);
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
