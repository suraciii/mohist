using System.CommandLine;
using Mohist.Cli.Tests.Compatibility;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Api;

public class ProjectCliRepositoryTests
{
    [Fact]
    public void RepoListHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["repo", "list", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
        Assert.Contains("--output", help);
    }

    [Fact]
    public void RepoAddHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["repo", "add", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
        Assert.Contains("--git-url", help);
        Assert.Contains("--set-default", help);
        Assert.DoesNotContain("--default", help);
    }

    [Fact]
    public void RepoUpdateHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["repo", "update", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Fact]
    public void RepoSetDefaultHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["repo", "set-default", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Fact]
    public void RepoDeleteHelp_DocumentsProjectAndProjectIdOptions()
    {
        var help = RenderHelp(["repo", "delete", "--help"]);

        Assert.Contains("--project", help);
        Assert.Contains("--project-id", help);
    }

    [Fact]
    public void ProjectHelp_DoesNotListRepoSubcommand()
    {
        var help = RenderHelp(["project", "--help"]);

        Assert.Contains("list", help);
        Assert.Contains("create", help);
        Assert.Contains("show", help);
        Assert.Contains("use", help);
        Assert.Contains("delete", help);
        Assert.DoesNotContain("repo", help);
    }

    [Fact]
    public void RepoHelp_DoesNotAdvertiseNameOption()
    {
        var help = RenderHelp(["repo", "add", "--help"]);

        Assert.DoesNotContain("--name", help);
        Assert.DoesNotContain("--path", help);
    }

    [Fact]
    public async Task RepoAdd_ConflictResponse_IsSurfacedAsNonZeroExit()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.Conflict, """
            { "success": false, "error": "Repository 'api' already exists" }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["repo", "add", "api", "--project", "mohist-local", "--git-url", "git@example.com:api.git"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Contains("api", error.ToString());
        Assert.Contains("already exists", error.ToString());
    }

    [Fact]
    public async Task RepoDelete_MissingRepositoryResponse_IsSurfacedAsNonZeroExit()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.NotFound, """
            { "success": false, "error": "Project or repository not found" }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["repo", "delete", "missing-repo", "--project", "mohist-local"],
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
