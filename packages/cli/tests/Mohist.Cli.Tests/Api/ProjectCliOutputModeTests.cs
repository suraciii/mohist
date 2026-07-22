using System.CommandLine;
using Mohist.Cli.Tests.Compatibility;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public class ProjectCliOutputModeTests
{
    [Fact]
    public void ProjectList_Help_ListsOutputOption()
    {
        var help = RenderHelp(["project", "list", "--help"]);

        Assert.Contains("--json", help);
    }

    [Fact]
    public void ProjectList_Help_DescribesJsonFieldSelection()
    {
        var help = RenderHelp(["project", "list", "--help"]);

        var jsonLine = help.Split('\n').FirstOrDefault(line => line.Contains("--json")) ?? "";
        Assert.Contains("selected fields", jsonLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectShow_Help_ListsOutputOption()
    {
        var help = RenderHelp(["project", "show", "--help"]);

        Assert.Contains("--json", help);
    }

    [Fact]
    public void ProjectShow_Help_DescribesJsonFieldSelection()
    {
        var help = RenderHelp(["project", "show", "--help"]);

        var jsonLine = help.Split('\n').FirstOrDefault(line => line.Contains("--json")) ?? "";
        Assert.Contains("selected fields", jsonLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProjectList_OutputTable_RendersProjectListTable()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "proj_1", "name": "mohist-local", "baseBranch": "main" }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list",],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var text = output.ToString();
        Assert.Contains("id", text);
        Assert.Contains("name", text);
        Assert.Contains("base branch", text);
        Assert.Contains("proj_1", text);
        Assert.Contains("mohist-local", text);
        Assert.Contains("main", text);
    }

    [Fact]
    public async Task ProjectList_OutputTable_MarksActiveProjectWithStar()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "proj_active", "name": "active-project", "baseBranch": "main" },
                { "id": "proj_other", "name": "other-project", "baseBranch": "main" }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(
            StatePath(),
            """{ "activeProjectId": "proj_active" }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list",],
            output,
            error,
            fileSystem,
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Split('\n').Where(l => l.Contains("proj_active") || l.Contains("proj_other")).ToList();
        Assert.NotEmpty(lines);
        var activeLine = lines.First(l => l.Contains("proj_active"));
        var otherLine = lines.First(l => l.Contains("proj_other"));
        Assert.Contains("*", activeLine);
        Assert.DoesNotContain("*", otherLine.TrimStart('*'));
    }

    [Fact]
    public async Task ProjectList_SelectedJson_IsIndependentFromHumanOutput()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "proj_1", "name": "mohist-local", "baseBranch": "main" }
              ]
            }
            """;

        var defaultHttp = new RecordingHttpHandler();
        defaultHttp.EnqueueJson(HttpStatusCode.OK, json);
        var defaultOutput = new StringWriter();
        var defaultError = new StringWriter();
        var defaultExit = await MohistCliCommands.RunAsync(
            new HttpClient(defaultHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list"],
            defaultOutput,
            defaultError,
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        var explicitHttp = new RecordingHttpHandler();
        explicitHttp.EnqueueJson(HttpStatusCode.OK, json);
        var explicitOutput = new StringWriter();
        var explicitError = new StringWriter();
        var explicitExit = await MohistCliCommands.RunAsync(
            new HttpClient(explicitHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list", "--json", "id"],
            explicitOutput,
            explicitError,
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(defaultExit, explicitExit);
        Assert.Equal("", defaultError.ToString());
        Assert.Equal("", explicitError.ToString());
        Assert.NotEqual(defaultOutput.ToString(), explicitOutput.ToString());
        Assert.Contains("\"id\": \"proj_1\"", explicitOutput.ToString());
        Assert.DoesNotContain("mohist-local", explicitOutput.ToString());
        Assert.Equal("/api/projects", defaultHttp.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects", explicitHttp.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ProjectList_OutputJsonAndTable_SendSameHttpRequest()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "proj_1", "name": "mohist-local", "baseBranch": "main" }
              ]
            }
            """;

        var jsonHttp = new RecordingHttpHandler();
        jsonHttp.EnqueueJson(HttpStatusCode.OK, json);
        await MohistCliCommands.RunAsync(
            new HttpClient(jsonHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list", "--json", "id"],
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        var tableHttp = new RecordingHttpHandler();
        tableHttp.EnqueueJson(HttpStatusCode.OK, json);
        await MohistCliCommands.RunAsync(
            new HttpClient(tableHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list",],
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        var jsonReq = jsonHttp.Requests.Single();
        var tableReq = tableHttp.Requests.Single();

        Assert.Equal(HttpMethod.Get, jsonReq.Method);
        Assert.Equal(HttpMethod.Get, tableReq.Method);
        Assert.Equal("/api/projects", jsonReq.RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects", tableReq.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ProjectList_LegacyOutputOption_FailsBeforeHttpCall()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "list", "--output", "yaml"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(2, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--output", err);
    }

    [Fact]
    public async Task ProjectShow_OutputTable_RendersProjectShowTable()
    {
        const string json = """
            {
              "success": true,
              "data": {
                "id": "proj_1",
                "name": "mohist-local",
                "baseBranch": "main",
                "repositories": [],
                "createdAt": "2024-01-01T00:00:00Z",
                "updatedAt": "2024-01-02T00:00:00Z"
              }
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "show", "mohist-local",],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var text = output.ToString();
        Assert.Contains("id:", text);
        Assert.Contains("name:", text);
        Assert.Contains("base branch:", text);
        Assert.Contains("proj_1", text);
        Assert.Contains("mohist-local", text);
        Assert.Contains("main", text);
    }

    [Fact]
    public async Task ProjectShow_LegacyOutputOption_FailsBeforeHttpCall()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "show", "mohist-local", "--output", "yaml"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            getUserHome: () => "/mohist-tests/user");

        Assert.Equal(2, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--output", err);
    }

    private static string StatePath()
    {
        const string home = "/mohist-tests/user";
        return System.IO.Path.Combine(home, ".mohist", "cli-state.json");
    }

    private static string RenderHelp(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(RejectingHttpMessageHandler.CreateClient(), TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(new FakeFileSystem());
        services.AddSingleton<ICommandExecutor>(
            new NoopCommandExecutor());
        services.AddSingleton<IServiceInstaller>(new SystemdServiceInstaller(TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor()));
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();
        services.AddSingleton<InfoCollector>();

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
