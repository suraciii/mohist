using System.Net;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliEventsDeadLetterCommandSpecs
{
    private static (HttpClient Http, RecordingHttpHandler Handler, StringWriter Output, StringWriter Error, FakeFileSystem FileSystem, FakeCommandExecutor Executor, MockEnvironmentVariableProvider Environment) Setup(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(responder);
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[OperatorCredentialProvider.TokenEnvironmentVariable] = "test-operator-token-0123456789abcdef";
        return (
            http,
            handler,
            output,
            error,
            fileSystem,
            executor,
            environment);
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
                    status = "Redelivering",
                    attempts = 3,
                    deadLetteredAt = "2026-07-11T01:00:00Z",
                    error = "temporary failure",
                },
            },
        })));

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["events", "dead-letter", "list", "--handler", "Handler+One", "--limit", "25"],
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
        Assert.Contains("Redelivering", env.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("temporary failure", env.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            "test-operator-token-0123456789abcdef",
            Assert.Single(request.Headers[OperatorCredentialProvider.HeaderName]));
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
            ["events", "dead-letter", "list", "--json", "id"],
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
            ["events", "dead-letter", "list", "--limit", "0"],
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
            ["events", "dead-letter", "redeliver", "17"],
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
        Assert.Equal(
            "test-operator-token-0123456789abcdef",
            Assert.Single(request.Headers[OperatorCredentialProvider.HeaderName]));
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
            ["events", "dead-letter", "redeliver", "17"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("handler still unavailable", env.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("dead_letter_redelivery_failed", env.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_MissingCredential_FailsBeforeCallingApi()
    {
        var env = Setup((_, _) => throw new InvalidOperationException("API must not be called"));
        env.Environment[OperatorCredentialProvider.TokenEnvironmentVariable] = "";

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["events", "dead-letter", "list"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(1, exitCode);
        Assert.Contains("operator credential was not found", env.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task List_NonLoopbackServerFailsBeforeReadingOrSendingCredential()
    {
        var env = Setup((_, _) => throw new InvalidOperationException("API must not be called"));
        env.Http.BaseAddress = new Uri("https://example.test");
        env.Environment[OperatorCredentialProvider.TokenEnvironmentVariable] = "";
        env.Environment["HOME"] = "/home/no-credential";

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["events", "dead-letter", "list"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(1, exitCode);
        Assert.Contains("loopback", env.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential was not found", env.Error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task List_DefaultCredentialFileAuthenticatesRequest()
    {
        var env = Setup((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Array.Empty<object>(),
        })));
        env.Environment[OperatorCredentialProvider.TokenEnvironmentVariable] = "";
        env.Environment["HOME"] = "/home/test";
        env.FileSystem.AddFile(
            "/home/test/.mohist/operator-token",
            "file-operator-token-0123456789abcdef");

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["events", "dead-letter", "list"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(
            "file-operator-token-0123456789abcdef",
            Assert.Single(request.Headers[OperatorCredentialProvider.HeaderName]));
    }

    [Fact]
    public async Task List_ConfiguredCredentialPathAuthenticatesRequest()
    {
        var env = Setup((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = Array.Empty<object>(),
        })));
        env.Environment[OperatorCredentialProvider.TokenEnvironmentVariable] = "";
        env.Environment[OperatorCredentialProvider.TokenPathEnvironmentVariable] = "";
        env.Environment["HOME"] = "/home/test";
        env.FileSystem.AddFile(
            "/home/test/.mohist/config.jsonc",
            """
            {
              // Shared by the server and mo CLI.
              "Mohist": {
                "OperatorTokenPath": "/run/mohist/operator-token",
              },
            }
            """);
        env.FileSystem.AddFile(
            "/run/mohist/operator-token",
            "configured-file-token-0123456789abcdef");

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["events", "dead-letter", "list"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(
            "configured-file-token-0123456789abcdef",
            Assert.Single(request.Headers[OperatorCredentialProvider.HeaderName]));
    }

    [Fact]
    public async Task List_Table_StripsTerminalControlSequencesFromUntrustedCells()
    {
        var env = Setup((_, _) => Task.FromResult(RecordingHttpHandler.Json(new
        {
            success = true,
            data = new[]
            {
                new
                {
                    id = 19,
                    type = "test.poison",
                    handler = "Handler.Control",
                    status = "Pending",
                    attempts = 3,
                    deadLetteredAt = "2026-07-11T01:00:00Z",
                    error = "visible\u001b[2J\u001b[31m red\u001b[0m\rhidden",
                },
            },
        })));

        var exitCode = await MohistCliCommands.RunAsync(
            env.Http,
            ["events", "dead-letter", "list"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);

        Assert.Equal(0, exitCode);
        var output = env.Output.ToString();
        Assert.Contains("visible red", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", output, StringComparison.Ordinal);
    }
}
