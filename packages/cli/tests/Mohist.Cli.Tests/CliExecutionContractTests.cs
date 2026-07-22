using System.Net;
using Mohist.Cli.Tests.Support;
using System.Text.Json.Nodes;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliExecutionContractTests
{
    [Fact]
    public async Task UnknownActionReturnsUsageExitAndNearestCommandHelpOnStderr()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["workflow", "does-not-exist"], output, error, fileSystem, executor);

        Assert.Equal(2, exit);
        Assert.Contains("does-not-exist", error.ToString());
        Assert.Contains("Usage:", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnknownOptionReturnsUsageExitWithoutCallingHttp()
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create();

        var exit = await MohistCliCommands.RunAsync(
            http, ["workflow", "get", "wr_1", "--not-an-option"], output, error, fileSystem, executor);

        Assert.Equal(2, exit);
        Assert.Contains("--not-an-option", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ResponseReader_PreservesCodedFailureAndDetails()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(
            RecordingHttpHandler.Json(new
            {
                success = false,
                error = "Rejected",
                code = "workflow_conflict",
                details = new { @object = "issue-7", state = "running", reason = "already active" },
            }, HttpStatusCode.Conflict)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri(CliTestFactory.BaseAddress) };

        var result = await new CliResponseReader(http).ReadAsync(HttpMethod.Post, "/api/test", new { }, mutating: true);

        Assert.False(result.IsSuccess);
        Assert.Equal("workflow_conflict", result.Failure!.Code);
        Assert.Equal("Rejected", result.Failure.Message);
        Assert.Equal("issue-7", result.Failure.Details!["object"]?.GetValue<string>());
        Assert.Equal(CliTransportAttemptState.Completed, result.Failure.AttemptState);
    }

    [Fact]
    public async Task ResponseReader_AssignsHttpFallbackToCodeLessFailure()
    {
        var handler = new RecordingHttpHandler((_, _) => Task.FromResult(
            RecordingHttpHandler.Json(new
            {
                success = false,
                error = "Unavailable",
                details = new { retryAfter = 10 },
            }, HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri(CliTestFactory.BaseAddress) };

        var result = await new CliResponseReader(http).ReadAsync(HttpMethod.Get, "/api/test");

        Assert.Equal("http-503", result.Failure!.Code);
        Assert.Equal("Unavailable", result.Failure.Message);
        Assert.Equal(10, result.Failure.Details!["retryAfter"]?.GetValue<int>());
    }

    [Fact]
    public async Task ResultWriter_KeepsResultsOnStdoutAndDiagnosticsOnStderr()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var invocation = new CliInvocation(
            output,
            error,
            TextReader.Null,
            new CliTerminal(false),
            new TestEnvironment());
        var writer = new CliResultWriter(invocation, new CliHintResolver(
            new Dictionary<string, string> { ["conflict"] = "mo issue resume 7" }));

        Assert.Equal(0, await writer.WriteSuccessAsync(new JsonObject { ["id"] = "x" }));
        Assert.Equal(1, await writer.WriteFailureAsync(new CliFailure("conflict", "Rejected", null)));

        Assert.Contains("\"id\"", output.ToString());
        Assert.DoesNotContain("Rejected", output.ToString());
        Assert.Contains("code=conflict", error.ToString());
        Assert.Contains("hint: mo issue resume 7", error.ToString());
    }

    [Fact]
    public async Task Invocation_DisabledPromptDoesNotReadOrRunPrompt()
    {
        var input = new ThrowingReader();
        var invocation = new CliInvocation(
            TextWriter.Null,
            new StringWriter(),
            input,
            new CliTerminal(false),
            new TestEnvironment(("MOHIST_PROMPT_DISABLED", "1")));

        var prompted = await invocation.RequirePromptAsync("confirmation", "--yes", () =>
        {
            throw new InvalidOperationException("prompt should not run");
        });

        Assert.False(prompted);
        Assert.Equal(0, input.ReadCount);
        Assert.Contains("--yes", invocation.Error.ToString());
    }

    [Fact]
    public async Task ResponseReader_ClassifiesMutatingTransportFailureAsUnknownWithoutRetry()
    {
        var handler = new RecordingHttpHandler((_, _) => throw new HttpRequestException("connection lost"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri(CliTestFactory.BaseAddress) };

        var result = await new CliResponseReader(http).ReadAsync(HttpMethod.Patch, "/api/test", new { }, mutating: true);

        Assert.Equal(CliTransportAttemptState.OutcomeUnknown, result.Failure!.AttemptState);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void TransportAttempt_ProvenUnsubmittedFailureIsRetryEligibleButUnknownIsNot()
    {
        Assert.Equal(
            CliTransportAttemptState.NotSubmitted,
            CliTransportAttempt.ClassifyFailure(mutating: true, sendStarted: false));
        Assert.True(CliTransportAttempt.ShouldRetry(CliTransportAttemptState.NotSubmitted));
        Assert.False(CliTransportAttempt.ShouldRetry(CliTransportAttemptState.OutcomeUnknown));
    }

    private sealed class TestEnvironment : ICliEnvironment
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public TestEnvironment(params (string Name, string Value)[] values) =>
            _values = values.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);

        public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class ThrowingReader : TextReader
    {
        public int ReadCount { get; private set; }

        public override int Read() => throw new InvalidOperationException($"read {++ReadCount}");
    }
}
