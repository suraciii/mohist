using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class CliProjectCommandSpecs
{
    [Fact]
    public async Task ProjectCreate_NameOnly_SendsBodyWithoutPathFields()
    {
        var handler = new RecordingHttpHandler(async (_, _) =>
        {
            return RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "proj_123", name = "my-project" },
            }, HttpStatusCode.Created);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fileSystem = new FakeFileSystem();
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "create", "my-project"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects", request.RequestUri?.PathAndQuery);

        var body = JsonNode.Parse(request.Body!);
        Assert.Equal("my-project", body!["name"]?.GetValue<string>());
        Assert.False(body.AsObject().ContainsKey("path"));
        Assert.False(body.AsObject().ContainsKey("effectivePath"));
        Assert.False(body.AsObject().ContainsKey("baseBranch"));
    }

    [Fact]
    public async Task ProjectList_DisplaysNamesAndCurrentMarkerWithoutPaths()
    {
        var handler = new RecordingHttpHandler(async (_, _) =>
        {
            return RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { id = "proj_a", name = "alpha" },
                    new { id = "proj_b", name = "beta" },
                },
            });
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3456") };
        var output = new StringWriter();
        var error = new StringWriter();
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mohist", "cli-state.json"),
            "{\"activeProjectId\":\"proj_b\"}");
        var executor = new FakeCommandExecutor();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "list", "--output", "table"], output, error, fileSystem, executor);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().TrimEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("  alpha", lines[0]);
        Assert.Equal("* beta", lines[1]);
        Assert.DoesNotContain("path", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
