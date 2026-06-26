using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.Workflow.Api;

[Collection("WorkflowCli")]
public class WorkflowCliListSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowHelp_ListsListSubcommand()
    {
        var help = await RenderHelp(["workflow", "--help"]);

        Assert.Contains("list", help);
        Assert.Contains("Workflow profile management", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowListHelp_DocumentsOutputOption()
    {
        var help = await RenderHelp(["workflow", "list", "--help"]);

        Assert.Contains("--output", help);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_Described_DisplaysAllProfilesWithIdAndDisplayName()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "mohist/local", "displayName": "Mohist Local", "description": "Default profile.", "suitableFor": ["feature development"] },
                { "id": "team/custom", "displayName": "Team Custom", "description": "Custom profile.", "suitableFor": [] }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, stderr) = await InvokeWorkflowAsync(http, "workflow", "list", "--described");

        Assert.True(exitCode == 0, $"exit={exitCode} stdout:\n{stdout}\n\nstderr:\n{stderr}");
        Assert.Contains("Mohist Local", stdout);
        Assert.Contains("Team Custom", stdout);
        Assert.Contains("mohist/local", stdout);
        Assert.Contains("team/custom", stdout);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_Described_PreservesMultiLineDescriptionFormatting()
    {
        const string multiLine = "Line one\nLine two\nLine three";
        var json = $$"""
            {
              "success": true,
              "data": [
                { "id": "mohist/local", "displayName": "Mohist Local", "description": {{JsonSerializer.Serialize(multiLine)}}, "suitableFor": [] }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, stderr) = await InvokeWorkflowAsync(http, "workflow", "list", "--described");

        Assert.True(exitCode == 0, $"exit={exitCode} stderr:\n{stderr}");
        Assert.Contains("Line one", stdout);
        Assert.Contains("Line two", stdout);
        Assert.Contains("Line three", stdout);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_JsonOutput_EmitsValidArrayWithRequiredFields()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "mohist/local", "name": "Mohist Local", "description": "Default profile.", "isDefault": true },
                { "id": "team/custom", "name": "Team Custom", "description": "Custom profile.", "isDefault": false }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, stderr) = await InvokeWorkflowAsync(http, "workflow", "list");

        Assert.True(exitCode == 0, $"exit={exitCode} stdout:\n{stdout}\n\nstderr:\n{stderr}");
        Assert.Equal(string.Empty, stderr);

        var parsed = JsonNode.Parse(stdout) as JsonArray;
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);

        var first = parsed[0]!.AsObject();
        Assert.Equal("mohist/local", first["id"]!.GetValue<string>());
        Assert.Equal("Mohist Local", first["name"]!.GetValue<string>());
        Assert.Equal("Default profile.", first["description"]!.GetValue<string>());
        Assert.True(first["isDefault"]!.GetValue<bool>());

        var second = parsed[1]!.AsObject();
        Assert.Equal("team/custom", second["id"]!.GetValue<string>());
        Assert.False(second["isDefault"]!.GetValue<bool>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_JsonOutput_PreservesMultiLineDescription()
    {
        const string multiLine = "Line one\nLine two\nLine three";
        var json = $$"""
            {
              "success": true,
              "data": [
                { "id": "mohist/local", "name": "Mohist Local", "description": {{JsonSerializer.Serialize(multiLine)}}, "isDefault": true }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, _) = await InvokeWorkflowAsync(http, "workflow", "list");

        Assert.True(exitCode == 0, $"exit={exitCode}");
        var parsed = JsonNode.Parse(stdout) as JsonArray;
        Assert.NotNull(parsed);
        var description = parsed![0]!.AsObject()["description"]!.GetValue<string>();
        Assert.Equal(multiLine, description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_JsonOutput_IsDefaultValueComesFromServer()
    {
        // Server returns isDefault=false for the default profile id.
        // The CLI must surface what the server says, not derive it locally.
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "mohist/local", "name": "Mohist Local", "description": "Default profile.", "isDefault": false }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, _) = await InvokeWorkflowAsync(http, "workflow", "list");

        Assert.True(exitCode == 0, $"exit={exitCode}");
        var parsed = JsonNode.Parse(stdout) as JsonArray;
        Assert.NotNull(parsed);
        var first = parsed![0]!.AsObject();
        Assert.Equal("mohist/local", first["id"]!.GetValue<string>());
        Assert.False(first["isDefault"]!.GetValue<bool>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_JsonOutput_IsSelfContained()
    {
        // The JSON output must contain only the documented fields per profile
        // and must not bleed in transient data like timestamps, paths, or env vars.
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "mohist/local", "name": "Mohist Local", "description": "Default profile.", "isDefault": true }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, _) = await InvokeWorkflowAsync(http, "workflow", "list");

        Assert.True(exitCode == 0, $"exit={exitCode}");
        var trimmed = stdout.Trim();
        var parsed = JsonNode.Parse(trimmed) as JsonArray;
        Assert.NotNull(parsed);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "name", "description", "isDefault",
        };
        foreach (var entry in parsed!)
        {
            var obj = entry!.AsObject();
            Assert.Equal(allowed, obj.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal));
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_CallsSystemTemplatesEndpoint()
    {
        const string json = """
            { "success": true, "data": [] }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, _, stderr) = await InvokeWorkflowAsync(http, "workflow", "list");

        Assert.True(exitCode == 0, $"exit={exitCode} stderr:\n{stderr}");
        var req = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/workflow-templates/system", req.RequestUri!.PathAndQuery);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_ServerNotRunning_ReportsStandardErrorAndExitsNonZero()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://127.0.0.1:1") },
            ["workflow", "list"],
            stdout,
            stderr,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        var err = stderr.ToString();
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Server is not running", err);
        Assert.Contains("mo server start", err);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_UnexpectedResponseShape_PassesRawDataThrough()
    {
        // --output json does raw passthrough of the API response data field;
        // shape validation is not performed for consistency with other commands.
        const string json = """
            { "success": true, "data": { "not": "an array" } }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, _) = await InvokeWorkflowAsync(http, "workflow", "list");

        Assert.Equal(0, exitCode);
        var parsed = JsonNode.Parse(stdout.Trim()) as JsonObject;
        Assert.NotNull(parsed);
        Assert.Equal("an array", parsed!["not"]!.GetValue<string>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowList_Described_SeparatesMultipleProfilesWithBlankLine()
    {
        const string json = """
            {
              "success": true,
              "data": [
                { "id": "mohist/local", "displayName": "Mohist Local", "description": "Default.", "suitableFor": [] },
                { "id": "team/custom", "displayName": "Team Custom", "description": "Custom.", "suitableFor": [] }
              ]
            }
            """;
        var http = new RecordingHttpHandler();
        http.EnqueueJson(HttpStatusCode.OK, json);

        var (exitCode, stdout, _) = await InvokeWorkflowAsync(http, "workflow", "list", "--described");

        Assert.True(exitCode == 0, $"exit={exitCode}");
        var text = stdout.Replace("\r\n", "\n");
        var defaultIndex = text.IndexOf("Mohist Local", StringComparison.Ordinal);
        var customIndex = text.IndexOf("Team Custom", StringComparison.Ordinal);
        Assert.True(defaultIndex >= 0);
        Assert.True(customIndex > defaultIndex);
        var between = text.Substring(defaultIndex, customIndex - defaultIndex);
        Assert.Contains("\n\n", between);
    }

    private static async Task<string> RenderHelp(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(new RecordingHttpHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            args,
            stdout,
            stderr,
            new FakeFileSystem(),
            new SystemCommandExecutor(),
            new MockEnvironmentVariableProvider());

        Assert.True(exitCode == 0, $"help exit={exitCode} stderr:\n{stderr}");
        return stdout.ToString();
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> InvokeWorkflowAsync(
        HttpMessageHandler handler,
        params string[] args)
    {
        return InvokeWithFilesAsync(new FakeFileSystem(), handler, args);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeWithFilesAsync(
        FakeFileSystem files,
        HttpMessageHandler handler,
        string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var environment = new MockEnvironmentVariableProvider();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") },
            args,
            stdout,
            stderr,
            files,
            new SystemCommandExecutor(),
            environment);

        return (exitCode, stdout.ToString(), stderr.ToString());
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

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Server is not running", new SocketException((int)SocketError.ConnectionRefused));
        }
    }
}
