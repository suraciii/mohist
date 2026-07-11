using System.Net;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliEventDeadLetterCommandSpecs
{
    private static (HttpClient Http, RecordingHttpHandler Handler, StringWriter Output, StringWriter Error, FakeFileSystem FileSystem, FakeCommandExecutor Executor, MockEnvironmentVariableProvider Environment) Setup(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(responder);
        return (
            http,
            handler,
            output,
            error,
            fileSystem,
            executor,
            new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false));
    }

    [Fact]
    public async Task List_Table_PrintsRowsAndEncodesHandlerFilter()
    {
        var env = Setup((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[]
            {
                new
                {
                    id = 17,
                    type = "com.mohist.issue.completed",
                    handler = "Handler+One",
                    attempts = 3,
                    deadLetteredAt = "2026-07-11T01:00:00Z",
                    error = "temporary failure",
                },
            },
        })));

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["event", "dead-letter", "list", "--handler", "Handler+One", "--limit", "25"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "/api/events/dead-letters?limit=25&handler=Handler%2BOne",
            request.RequestUri?.PathAndQuery);
        Assert.Contains("com.mohist.issue.completed", env.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("temporary failure", env.Output.ToString(), StringComparison.Ordinal);
        Assert.Empty(env.Error.ToString());
    }

    [Fact]
    public async Task List_Json_PrintsResponseData()
    {
        var env = Setup((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[] { new { id = 18, handler = "Handler.Two" } },
        })));

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["event", "dead-letter", "list", "-o", "json"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(0, exitCode);
        var data = JsonNode.Parse(env.Output.ToString()) as JsonArray;
        Assert.NotNull(data);
        Assert.Equal(18, data![0]!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task List_InvalidLimit_FailsBeforeCallingApi()
    {
        var env = Setup((_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["event", "dead-letter", "list", "--limit", "0"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(1, exitCode);
        Assert.Contains("between 1 and 500", env.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task Redeliver_PostsAndReportsDeliveryResult()
    {
        var env = Setup((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new { id = 17, delivered = true, attempts = 2, error = (string?)null },
        })));

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["event", "dead-letter", "redeliver", "17"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/events/dead-letters/17/redeliver", request.RequestUri?.PathAndQuery);
        Assert.Equal("{}", request.Body);
        Assert.Contains("Dead-letter 17: delivered after 2 attempt(s)", env.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeliver_ApiFailure_ReturnsNonZeroAndPrintsCode()
    {
        var env = Setup((_, _) => Task.FromResult(RecordingHttpHandler.JsonError(
            "handler still unavailable",
            "dead_letter_redelivery_failed",
            HttpStatusCode.Conflict)));

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["event", "dead-letter", "redeliver", "17"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("handler still unavailable", env.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("dead_letter_redelivery_failed", env.Error.ToString(), StringComparison.Ordinal);
    }
}
