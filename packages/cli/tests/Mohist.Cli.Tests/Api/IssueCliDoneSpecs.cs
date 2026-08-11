using System.Net;
using System.Text.Json;
using Mohist.Cli.Tests.Compatibility;
using Xunit;
using RecordingHttpHandler = Mohist.Cli.Tests.Support.RecordingHttpHandler;

namespace Mohist.Cli.Tests.Api;

public class IssueCliDoneSpecs
{
    [Fact]
    public async Task IssueDone_PostsToProjectScopedRoute()
    {
        var http = new RecordingHttpHandler((_, _) => Task.FromResult(
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    number = 410,
                    title = "Delivered outside workflow",
                    status = "done",
                    workflowRunId = (string?)null,
                },
            }, HttpStatusCode.OK)));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "done", "410", "--project", "mohist-local"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/mohist-local/issues/410/done", request.RequestUri!.PathAndQuery);
        Assert.Contains("done", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("done", "done")]
    [InlineData("close", "cancelled")]
    [InlineData("reopen", "backlog")]
    public async Task IssueLifecycleMutation_WithCanonicalResource_ProjectsSelectedFields(
        string action,
        string status)
    {
        var http = new RecordingHttpHandler((_, _) => Task.FromResult(
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    number = 410,
                    title = "Lifecycle resource",
                    status,
                    unrelated = "not selected",
                },
            }, HttpStatusCode.OK)));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", action, "410", "--project", "mohist-local", "--json", "number,title,status"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var fields = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "number", "title", "status" }, fields);
        Assert.Equal(410, document.RootElement.GetProperty("number").GetInt32());
        Assert.Equal("Lifecycle resource", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task IssueLifecycleMutation_WithSuccessfulNullData_ReturnsInvalidResponse()
    {
        var http = new RecordingHttpHandler((_, _) => Task.FromResult(
            RecordingHttpHandler.Json(new
            {
                success = true,
                data = (object?)null,
            }, HttpStatusCode.OK)));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "close", "410", "--project", "mohist-local", "--json", "number,status"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid-response", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("number", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueLifecycleMutation_WithServerError_PreservesServerFailure()
    {
        var http = new RecordingHttpHandler((_, _) => Task.FromResult(
            RecordingHttpHandler.Json(new
            {
                success = false,
                error = "issue cannot be reopened",
                code = "conflict",
            }, HttpStatusCode.Conflict)));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(http) { BaseAddress = new Uri("http://localhost:3456") },
            ["issue", "reopen", "410", "--project", "mohist-local", "--json", "number,status"],
            output,
            error,
            new FakeFileSystem(),
            new NoopCommandExecutor());

        Assert.Equal(1, exitCode);
        Assert.Contains("issue cannot be reopened", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("conflict", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-response", error.ToString(), StringComparison.Ordinal);
    }
}
