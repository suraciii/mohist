using System.CommandLine;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Api;

public class WorkflowCliSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void WorkflowList_Help_ListsDescribedOption()
    {
        var help = RenderHelp(["workflow", "list", "--help"]);

        Assert.Contains("--described", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowList_Described_RoutesToWorkflowProfilesEndpoint()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/default", "displayName": "Mohist Default", "description": "Plan, build, check, and integrate an issue using OpenSpec artifacts.", "suitableFor": ["feature development", "bug fixes"] } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["workflow", "list", "--described"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/workflow-profiles", req.RequestUri!.PathAndQuery);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowList_Described_FormatsHumanReadableOutput()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/default", "displayName": "Mohist Default", "description": "Plan, build, check, and integrate an issue using OpenSpec artifacts.", "suitableFor": ["feature development", "bug fixes"] } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["workflow", "list", "--described"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("mohist/default", stdout);
        Assert.Contains("Mohist Default", stdout);
        Assert.Contains("OpenSpec", stdout);
        Assert.Contains("feature development", stdout);
        Assert.Contains("bug fixes", stdout);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowList_Described_EmptySuitableFor_ShowsNotSpecified()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/default", "displayName": "Mohist Default", "description": "A workflow.", "suitableFor": [] } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["workflow", "list", "--described"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("not specified", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowList_WithoutDescribed_RoutesToExistingEndpoint()
    {
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, """
            { "success": true, "data": [ { "id": "mohist/default", "name": "Mohist Default", "description": "A workflow." } ] }
            """);

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["workflow", "list"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var req = http.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/workflow-templates/system", req.RequestUri!.PathAndQuery);
    }

    private static string RenderHelp(string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MohistCliApi(new HttpClient(), TextWriter.Null, TextWriter.Null, RealFileSystem.Instance, new SystemCommandExecutor()));
        services.AddSingleton<TextWriter>(TextWriter.Null);
        services.AddSingleton<IFileSystem>(RealFileSystem.Instance);
        services.AddSingleton<ICommandExecutor>(new SystemCommandExecutor());
        services.AddSingleton<IServiceInstaller, SystemdServiceInstaller>();
        services.AddSingleton<SystemdServiceInstaller>();
        services.AddSingleton<SourceCodeUpdater>();
        services.AddSingleton<SkillAssetService>();
        services.AddSingleton<SkillInstallService>();

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
