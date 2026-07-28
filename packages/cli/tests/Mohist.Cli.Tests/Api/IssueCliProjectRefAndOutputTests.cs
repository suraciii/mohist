using System.CommandLine;
using Mohist.Cli.Tests.Compatibility;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public class IssueCliProjectRefAndOutputTests
{
    [Fact]
    public void IssueList_Help_ListsProjectReferenceAndJsonOptions()
    {
        var help = RenderHelp(["issue", "list", "--help"]);

        Assert.Contains("--project", help);
        Assert.DoesNotContain("--project-id", help);
        Assert.Contains("--json", help);
    }

    [Fact]
    public void IssueView_Help_ListsProjectReferenceAndJsonOptions()
    {
        var help = RenderHelp(["issue", "view", "--help"]);

        Assert.Contains("--project", help);
        Assert.DoesNotContain("--project-id", help);
        Assert.Contains("--json", help);
    }

    [Fact]
    public void SessionList_Help_ListsProjectReferenceAndOutputOptions()
    {
        // `mo issue sessions <num>` was retired by issue-479 T-005; the
        // list is now `mo session list --issue <num>`. The unified command
        // inherits the same --project / --json contract.
        var help = RenderHelp(["session", "list", "--help"]);

        Assert.Contains("--project", help);
        Assert.DoesNotContain("--project-id", help);
        Assert.Contains("--json", help);
    }

    [Fact]
    public void IssueList_Help_OutputOptionDefaultsToJson()
    {
        var help = RenderHelp(["issue", "list", "--help"]);

        Assert.Contains("--json", help);
    }

    [Fact]
    public void IssueView_Help_OutputOptionDefaultsToJson()
    {
        var help = RenderHelp(["issue", "view", "--help"]);

        Assert.Contains("--json", help);
    }

    [Fact]
    public async Task IssueView_ByProjectName_SendsGetOnResolvedPath()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": { "id": "issue_1", "number": 83, "title": "Test" } }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "view", "83", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/83", req.RequestUri!.PathAndQuery);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task IssueView_ByProjectReference_SendsGetOnResolvedPath()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": { "id": "issue_1", "number": 83, "title": "Test" } }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "view", "83", "--project", "proj_f6c141d63b6243bfbb481737b2243b87"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects/proj_f6c141d63b6243bfbb481737b2243b87/issues/83", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueView_ProjectReference_ResolvesThroughSharedHelper()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": { "id": "issue_1", "number": 83, "title": "Test" } }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "view", "83", "--project", "proj_f6c141d63b6243bfbb481737b2243b87"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/proj_f6c141d63b6243bfbb481737b2243b87/issues/83", http.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task IssueList_ByProjectName_SendsGetOnResolvedPath()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": [] }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/mohist-local/issues", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task SessionList_ByIssue_ByProjectName_SendsGetOnResolvedPath()
    {
        // `mo issue sessions <num>` was retired; the unified list is
        // `mo session list --issue <num>` (issue-479 T-005).
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": [] }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["session", "list", "--issue", "83", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("/api/projects/mohist-local/sessions?issue=83", http.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueList_OutputTable_RendersIssueListTable()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "number": 1, "title": "alpha", "workflowStage": "build", "status": "in_progress", "priority": "p1" }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal("", error.ToString());
        var text = output.ToString();
        Assert.Contains("number", text);
        Assert.Contains("title", text);
        Assert.Contains("alpha", text);
        Assert.Contains("build", text);
        Assert.Contains("p1", text);
    }

    [Fact]
    public async Task IssueList_SelectedJsonProjectsOnlyRequestedFields()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "number": 1, "title": "alpha", "workflowStage": "build", "status": "in_progress", "priority": "p1" }
              ]
            }
            """;

        var defaultHttp = new RecordingHttpHandler();
        defaultHttp.EnqueueJson(HttpStatusCode.OK, json);
        var defaultOutput = new StringWriter();
        var defaultError = new StringWriter();
        var defaultExit = await MohistCliCommands.RunAsync(
            new HttpClient(defaultHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local"],
            defaultOutput,
            defaultError,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        var explicitHttp = new RecordingHttpHandler();
        explicitHttp.EnqueueJson(HttpStatusCode.OK, json);
        var explicitOutput = new StringWriter();
        var explicitError = new StringWriter();
        var explicitExit = await MohistCliCommands.RunAsync(
            new HttpClient(explicitHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local", "--json", "number,title"],
            explicitOutput,
            explicitError,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, defaultExit);
        Assert.Equal(0, explicitExit);
        Assert.Equal("", defaultError.ToString());
        Assert.Equal("", explicitError.ToString());
        Assert.NotEqual(defaultOutput.ToString(), explicitOutput.ToString());
        Assert.Equal("/api/projects/mohist-local/issues", defaultHttp.Requests.Single().RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects/mohist-local/issues", explicitHttp.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueList_OutputJsonAndTable_SendSameHttpRequest()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "number": 1, "title": "alpha", "workflowStage": "build", "status": "in_progress", "priority": "p1" }
              ]
            }
            """;

        var jsonHttp = new RecordingHttpHandler();
        jsonHttp.EnqueueJson(HttpStatusCode.OK, json);
        await MohistCliCommands.RunAsync(
            new HttpClient(jsonHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local", "--json", "number,title"],
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        var tableHttp = new RecordingHttpHandler();
        tableHttp.EnqueueJson(HttpStatusCode.OK, json);
        await MohistCliCommands.RunAsync(
            new HttpClient(tableHttp) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local"],
            new StringWriter(),
            new StringWriter(),
            new FakeFileSystem(),
            new NoopCommandExecutor());

        var jsonReq = jsonHttp.Requests.Single();
        var tableReq = tableHttp.Requests.Single();

        Assert.Equal(HttpMethod.Get, jsonReq.Method);
        Assert.Equal(HttpMethod.Get, tableReq.Method);
        Assert.Equal("/api/projects/mohist-local/issues", jsonReq.RequestUri!.PathAndQuery);
        Assert.Equal("/api/projects/mohist-local/issues", tableReq.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueList_LegacyOutput_FailsBeforeHttpCall()
    {
        var http = new RecordingHttpHandler();

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local", "--output", "yaml"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(2, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--json", err);
    }

    [Fact]
    public async Task IssueView_NoActiveProjectAndNoOption_PrintsGuidedDiagnostic()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "view", "83"],
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

    [Fact]
    public async Task IssueList_ConflictingProjectReferences_FailsWithGuidedError()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "list", "--project", "mohist-local", "--project-id", "proj_other"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(2, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("Unrecognized command or argument '--project-id'", err);
    }

    private static string RenderHelp(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(RejectingHttpMessageHandler.CreateClient(), TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(new FakeFileSystem());
        services.AddSingleton<ICommandExecutor>(new NoopCommandExecutor());
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
