using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliEventsTailCommandSpecs : IDisposable
{
    public CliEventsTailCommandSpecs()
    {
        EventCommands.TailCancellationOverride = default;
    }

    public void Dispose()
    {
        EventCommands.TailCancellationOverride = default;
    }

    [Fact]
    public async Task Tail_NoMatch_StreamsProjectEnvelopesOnePerLine()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/events/tail", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true }));
            return Task.FromResult(RecordingHttpHandler.Ndjson(new[]
            {
                "{\"type\":\"com.mohist.issue.completed\",\"source\":\"mohist.test\",\"id\":\"e1\",\"time\":\"2026-07-17T00:00:00Z\",\"specversion\":\"1.0\",\"subject\":\"42\",\"extensions\":{\"projectid\":\"proj_abc\"}}",
                "{\"type\":\"com.mohist.workflow.advanced\",\"source\":\"mohist.test\",\"id\":\"e2\",\"time\":\"2026-07-17T00:00:01Z\",\"specversion\":\"1.0\",\"extensions\":{\"projectid\":\"proj_abc\"}}",
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["events", "tail"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_abc/events/tail", request.RequestUri?.PathAndQuery);
        var stdout = output.ToString();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var first = JsonNode.Parse(lines[0]) as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("com.mohist.issue.completed", first!["type"]!.GetValue<string>());
        Assert.Equal("e1", first["id"]!.GetValue<string>());
        var second = JsonNode.Parse(lines[1]) as JsonObject;
        Assert.Equal("com.mohist.workflow.advanced", second!["type"]!.GetValue<string>());
        Assert.DoesNotContain("[", stdout, StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Tail_WithMatch_ForwardsMatchExpressionAndStreamsFilter()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var receivedMatch = string.Empty;
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/events/tail", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true }));
            receivedMatch = req.RequestUri.Query;
            return Task.FromResult(RecordingHttpHandler.Ndjson(new[]
            {
                "{\"type\":\"com.mohist.issue.completed\",\"source\":\"mohist.test\",\"id\":\"e1\",\"time\":\"2026-07-17T00:00:00Z\",\"specversion\":\"1.0\",\"extensions\":{\"issue\":\"42\"}}",
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["events", "tail", "--match", "event.type == \"com.mohist.issue.completed\" && event.issue in [\"42\", \"43\"]"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exitCode);
        Assert.Equal("?match=event.type%20%3D%3D%20%22com.mohist.issue.completed%22%20%26%26%20event.issue%20in%20%5B%2242%22%2C%20%2243%22%5D",
            receivedMatch);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var single = Assert.Single(lines);
        Assert.Contains("\"type\":\"com.mohist.issue.completed\"", single, StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Tail_InvalidMatch_PrintsLocationToStderrAndExitsNonZeroBeforeStreaming()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/events/tail", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true }));
            return Task.FromResult(RecordingHttpHandler.MatchCompileError(
                message: "Unbalanced '('",
                offset: 19,
                line: 1,
                column: 20,
                source: "(event.type == \"x\""));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["events", "tail", "--match", "(event.type == \"x\""],
            output,
            error,
            fs,
            executor);

        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains("Unbalanced '('", stderr, StringComparison.Ordinal);
        Assert.Contains("line 1", stderr, StringComparison.Ordinal);
        Assert.Contains("column 20", stderr, StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Tail_NoActiveProject_FailsLocallyWithoutContactingServer()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
        var called = false;
        handler.SetResponder((_, _) =>
        {
            called = true;
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["events", "tail"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.False(called);
        Assert.Empty(handler.Requests);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tail_NoActiveProject_WithMatch_FailsLocallyWithoutContactingServer()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
        var called = false;
        handler.SetResponder((_, _) =>
        {
            called = true;
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["events", "tail", "--match", "event.type == \"x\""], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.False(called);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Tail_ProjectFlagOverridesActiveProject()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/events/tail", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true }));
            return Task.FromResult(RecordingHttpHandler.Ndjson(Array.Empty<string>()));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["events", "tail", "--project", "proj_other"],
            output,
            error,
            fs,
            executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_other/events/tail", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task Tail_CancellationStopsStreamingAndReleasesRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        var observedToken = default(CancellationToken);
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();
        EventCommands.TailCancellationOverride = cts.Token;
        handler.SetResponder((req, token) =>
        {
            observedToken = token;
            block.TrySetResult();
            return WaitForCancelAsync(token, releaseSignal);
        });

        var runTask = MohistCliCommands.RunAsync(
            http, ["events", "tail"], output, error, fs, executor);

        await block.Task;
        cts.Cancel();
        await releaseSignal.Task;
        var exitCode = await runTask;

        Assert.Equal(0, exitCode);
        Assert.True(observedToken.IsCancellationRequested);
        Assert.Empty(output.ToString());
        cts.Dispose();
    }

    private static async Task<HttpResponseMessage> WaitForCancelAsync(
        CancellationToken token,
        TaskCompletionSource release)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => tcs.TrySetResult());
        await tcs.Task;
        release.TrySetResult();
        throw new OperationCanceledException(token);
    }

    [Fact]
    public async Task Tail_SingularNoun_DoesNotResolve()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["event", "tail"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}