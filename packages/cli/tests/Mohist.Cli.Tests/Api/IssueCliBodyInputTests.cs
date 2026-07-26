using System.CommandLine;
using Mohist.Cli.Tests.Compatibility;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public class IssueCliBodyInputTests
{
    [Fact]
    public void IssueCreate_Help_ListsCanonicalBodyOptions()
    {
        var help = RenderHelp(["issue", "create", "--help"]);

        Assert.Contains("--body", help);
        Assert.Contains("--body-file", help);
        Assert.DoesNotContain("--body-stdin", help);
        Assert.Contains("mutually exclusive", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IssueUpdate_Help_ListsCanonicalBodyOptions()
    {
        var help = RenderHelp(["issue", "edit", "--help"]);

        Assert.Contains("--body", help);
        Assert.Contains("--body-file", help);
        Assert.DoesNotContain("--body-stdin", help);
        Assert.Contains("mutually exclusive", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueCreate_InlineBody_SendsLiteralBodyUnchanged()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body", "literal markdown body", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues", req.RequestUri!.PathAndQuery);
        var body = http.ReadCapturedBody(req);
        Assert.Equal("literal markdown body", body?["body"]?.GetValue<string>());
        Assert.Equal("Title", body?["title"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_BodyFile_ReadsFileAndSendsContents()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "# hello\nfrom file\n");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "body.md", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("# hello\nfrom file\n", body?["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_BodyFileDash_DrainsStdinAndSendsContents()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();
        var stdin = new StringReader("piped body content\nmore");

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "-", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor(),
            standardInput: stdin);

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("piped body content\nmore", body?["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_NoBodySource_WritesErrorAndExitsNonZeroWithNoHttpCall()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        Assert.Contains("issue body is required", error.ToString());
    }

    [Fact]
    public async Task IssueCreate_InlineAndFile_WritesMutualExclusionErrorAndExitsNonZeroWithNoHttpCall()
    {
        var files = new FakeFileSystem();
        files.AddFile("b.md", "from file");
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body", "a", "--body-file", "b.md", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--body", err);
        Assert.Contains("--body-file", err);
        Assert.Contains("mutually exclusive", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueCreate_MissingBodyFile_WritesFileReadErrorAndExitsOneWithNoHttpCall()
    {
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "missing.md", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("could not read body file", err);
        Assert.Contains("missing.md", err);
    }

    [Fact]
    public async Task IssueUpdate_BodyFile_ReadsFileAndSendsContents()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "# updated body\n");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "edit", "1", "--body-file", "body.md", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Patch, req.Method);
        Assert.Equal("/api/projects/mohist-local/issues/1", req.RequestUri!.PathAndQuery);
        var body = http.ReadCapturedBody(req);
        Assert.Equal("# updated body\n", body?["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueUpdate_InlineAndFile_WritesMutualExclusionErrorAndExitsNonZeroWithNoHttpCall()
    {
        var files = new FakeFileSystem();
        files.AddFile("b.md", "from file");
        var http = new RecordingHttpHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "edit", "1", "--body", "a", "--body-file", "b.md", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Empty(http.Requests);
        var err = error.ToString();
        Assert.Contains("--body", err);
        Assert.Contains("--body-file", err);
        Assert.Contains("mutually exclusive", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IssueCreate_Help_ListsRiskOption()
    {
        var help = RenderHelp(["issue", "create", "--help"]);

        Assert.Contains("--risk", help);
    }

    [Fact]
    public async Task IssueCreate_BodyFileWithFrontmatter_AutoFillsWorkflowAndRiskAndStripsBlock()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md",
            "---\n"
            + "recommended_workflow: feature-flow\n"
            + "recommended_workflow_reason: Matches scope\n"
            + "risk: high\n"
            + "---\n"
            + "## Background\nReal content.\n");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "body.md", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("feature-flow", body?["workflowProfileId"]?.GetValue<string>());
        Assert.Equal("high", body?["risk"]?.GetValue<string>());
        Assert.Equal("## Background\nReal content.\n", body?["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_ExplicitWorkflowProfileOverridesFrontmatter()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md",
            "---\n"
            + "recommended_workflow: feature-flow\n"
            + "risk: low\n"
            + "---\n"
            + "Body.\n");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "body.md", "--workflow-profile", "mohist/local", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("mohist/local", body?["workflowProfileId"]?.GetValue<string>());
        Assert.Equal("low", body?["risk"]?.GetValue<string>());
        Assert.Contains("overrides frontmatter recommended_workflow", error.ToString());
    }

    [Fact]
    public async Task IssueCreate_BodyFileWithoutFrontmatter_EmitsWarningButSucceeds()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md", "# plain body\nno frontmatter\n");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "body.md", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("# plain body\nno frontmatter\n", body?["body"]?.GetValue<string>());
        Assert.Null(body?["workflowProfileId"]?.GetValue<string>());
        Assert.Contains("no frontmatter found", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueCreate_MalformedFrontmatter_EmitsWarningButSendsFullBody()
    {
        var files = new FakeFileSystem();
        var fullBody = "---\nrecommended_workflow feature-flow\n---\nBody after block.\n";
        files.AddFile("body.md", fullBody);
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "body.md", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal(fullBody, body?["body"]?.GetValue<string>());
        Assert.Null(body?["workflowProfileId"]?.GetValue<string>());
        Assert.Contains("malformed", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueCreate_RiskFlag_SentInCreateRequest()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body", "x", "--risk", "medium", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("medium", body?["risk"]?.GetValue<string>());
        Assert.Null(body?["workflowProfileId"]?.GetValue<string>());
    }

    [Fact]
    public async Task IssueCreate_ExplicitRiskFlagOverridesFrontmatterRisk()
    {
        var files = new FakeFileSystem();
        files.AddFile("body.md",
            "---\n"
            + "recommended_workflow: feature-flow\n"
            + "risk: low\n"
            + "---\n"
            + "Body.\n");
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """{ "success": true, "data": { "id": "issue_1", "number": 1 } }""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "create", "Title", "--body-file", "body.md", "--risk", "high", "--project", "mohist-local"],
            output,
            error,
            files,
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var body = http.ReadCapturedBody(http.Requests.Single());
        Assert.Equal("feature-flow", body?["workflowProfileId"]?.GetValue<string>());
        Assert.Equal("high", body?["risk"]?.GetValue<string>());
        Assert.Contains("overrides frontmatter risk", error.ToString());
    }

    private static string RenderHelp(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(RejectingHttpMessageHandler.CreateClient(), TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(new FakeFileSystem());
        services.AddSingleton<ICommandExecutor>(new NoopCommandExecutor());
        services.AddSingleton<IServiceInstaller>(_ => new SystemdServiceInstaller(TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor()));
        services.AddSingleton<SystemdServiceInstaller>();
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
