using System.CommandLine;
using Mohist.Server.UnitTests.Support;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public class WorkflowCliProfileTests
{
    private const string DefaultProjectId = "proj_abc";

    [Fact]
    public void ProfileList_Help_ListsDescribedOption()
    {
        var help = RenderHelp(["project", "workflow", "profile", "list", "--help"]);

        Assert.Contains("--described", help);
    }

    [Fact]
    public async Task ProfileList_Described_RoutesToWorkflowProfilesEndpoint()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/local", "displayName": "Mohist Local", "description": "Plan, build, check, and integrate an issue using OpenSpec artifacts." } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "workflow", "profile", "list", "--described", "--project", DefaultProjectId],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal($"/api/workflow-profiles?project={DefaultProjectId}", req.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ProfileList_Described_FormatsHumanReadableOutput()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/local", "displayName": "Mohist Local", "description": "Plan, build, check, and integrate an issue using OpenSpec artifacts." } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "workflow", "profile", "list", "--described", "--project", DefaultProjectId],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("mohist/local", stdout);
        Assert.Contains("Mohist Local", stdout);
        Assert.Contains("OpenSpec", stdout);
        Assert.DoesNotContain("Suitable for", stdout);
        Assert.DoesNotContain("not specified", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileList_Described_DescriptionIsShownAndNoSuitableForLineIsEmitted()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/local", "displayName": "Mohist Local", "description": "A workflow." } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "workflow", "profile", "list", "--described", "--project", DefaultProjectId],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("A workflow.", stdout);
        Assert.DoesNotContain("Suitable for", stdout);
        Assert.DoesNotContain("not specified", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileList_WithoutDescribed_RoutesToExistingEndpoint()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/local", "name": "Mohist Local", "description": "A workflow." } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["project", "workflow", "profile", "list", "--project", DefaultProjectId],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal($"/api/workflow-templates/system?project={DefaultProjectId}", req.RequestUri!.PathAndQuery);
    }

    private static string RenderHelp(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(RejectingHttpMessageHandler.CreateClient(), TextWriter.Null, TextWriter.Null, new FakeFileSystem(), new NoopCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(new FakeFileSystem());
        services.AddSingleton<ICommandExecutor>(new NoopCommandExecutor());
        services.AddSingleton<IServiceInstaller, SystemdServiceInstaller>();
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