using System.CommandLine;
using Mohist.Server.UnitTests.Support;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public class IssueCliRemainingProjectRefTests
{
    [Fact]
    public void IssueCreate_Help_ListsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["issue", "create", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Fact]
    public void IssueUpdate_Help_ListsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["issue", "update", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("approve")]
    [InlineData("close")]
    [InlineData("reopen")]
    [InlineData("retry")]
    [InlineData("rerun")]
    [InlineData("rerun-from-stage")]
    [InlineData("force-stop")]
    [InlineData("resume")]
    [InlineData("rebase")]
    [InlineData("archive")]
    [InlineData("unarchive")]
    [InlineData("logs")]
    [InlineData("events")]
    [InlineData("diff")]
    [InlineData("commits")]
    public void IssueSubcommand_Help_ListsProjectAndProjectIdOptions(string subcommand)
    {
        var help = RenderHelp(["issue", subcommand, "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Fact]
    public void IssueWorkflowTimeline_Help_ListsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["issue", "workflow", "timeline", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Fact]
    public void IssueCreate_Help_DoesNotAdvertiseOutputOption()
    {
        var help = RenderHelp(["issue", "create", "--help"]);

        Assert.DoesNotContain("--output", help);
    }

    [Fact]
    public void IssueUpdate_Help_DoesNotAdvertiseOutputOption()
    {
        var help = RenderHelp(["issue", "update", "--help"]);

        Assert.DoesNotContain("--output", help);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("close")]
    [InlineData("retry")]
    [InlineData("rebase")]
    [InlineData("logs")]
    [InlineData("commits")]
    public void IssueSubcommand_Help_DoesNotAdvertiseOutputOption(string subcommand)
    {
        var help = RenderHelp(["issue", subcommand, "--help"]);

        Assert.DoesNotContain("--output", help);
    }

    [Fact]
    public void IssueWorkflowTimeline_Help_DoesNotAdvertiseOutputOption()
    {
        var help = RenderHelp(["issue", "workflow", "timeline", "--help"]);

        Assert.DoesNotContain("--output", help);
    }

    [Fact]
    public async Task IssueClose_ByProjectName_SendsPostToResolvedProjectRoute()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "number": 83, "status": "closed" } }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "close", "83", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/83/close", req.RequestUri!.PathAndQuery);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task IssueClose_ByProjectIdAlias_StillResolvesThroughSharedHelper()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "number": 83, "status": "closed" } }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "close", "83", "--project-id", "proj_f6c141d63b6243bfbb481737b2243b87"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/proj_f6c141d63b6243bfbb481737b2243b87/issues/83/close", req.RequestUri!.PathAndQuery);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task IssueCreate_ByProjectName_SendsPostToResolvedProjectRoute()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Created, """{ "success": true, "data": { "number": 84, "title": "Test" } }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Test issue", "--body", "body content", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueUpdate_ByProjectName_SendsPatchToResolvedProjectRoute()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "number": 83, "title": "Updated" } }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "update", "83", "--title", "Updated", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/83", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueLogs_ByProjectName_SendsGetToResolvedProjectRoute()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "logs": [] } }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "logs", "83", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/83/logs", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueWorkflowTimeline_ByProjectName_SendsGetToResolvedProjectRoute()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "events": [] } }""");

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "workflow", "timeline", "83", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/83/workflow/timeline", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task IssueClose_ConflictingProjectAndProjectId_FailsWithGuidedError()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "close", "83", "--project", "mohist-local", "--project-id", "proj_other"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("mohist-local", err);
        Assert.Contains("proj_other", err);
        Assert.Contains("--project", err);
        Assert.Contains("--project-id", err);
    }

    [Fact]
    public async Task IssueClose_NoActiveProjectAndNoOption_PrintsGuidedDiagnostic()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "close", "83"],
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
