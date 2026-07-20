using System.Net;
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
            RecordingHttpHandler.Json(new { success = true }, HttpStatusCode.OK)));
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
        Assert.Equal(string.Empty, error.ToString());
    }
}
