using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using EnvironmentAbstractions.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliEventTailCommandSpecs : IDisposable
{
    private const string Ack = """{"jsonrpc":"2.0","id":"req_1","result":{}}""";
    private const string DomainEvent = """{"jsonrpc":"2.0","method":"event.domain","params":{"event":{"specversion":"1.0","id":"e1","source":"/mohist/projects/proj_abc/issues/42","type":"com.mohist.issue.completed","datacontenttype":"application/json","data":{},"projectid":"proj_abc","issue":"42"}}}""";

    public CliEventTailCommandSpecs() => EventCommands.TailCancellationOverride = default;

    public void Dispose() => EventCommands.TailCancellationOverride = default;

    [Fact]
    public async Task Tail_SendsDomainSubscriptionAndEmitsStandardEventObject()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory) { OnExhausted = cts.Cancel }
            .AddJson(Ack)
            .AddJson("""{"jsonrpc":"2.0","method":"event.transcript","params":{"event":{}}}""")
            .AddJson(DomainEvent);
        factory.Add(socket);

        var result = await RunAsync(factory, cts.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal("ws://localhost:3456/api/projects/proj_abc/events/socket", Assert.Single(factory.Endpoints).ToString());
        var subscription = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        Assert.Equal("subscription.set", subscription["method"]!.GetValue<string>());
        Assert.Null(subscription["params"]!["domain"]!["types"]);
        Assert.Null(subscription["params"]!["domain"]!["match"]);
        Assert.Null(subscription["params"]!["transcript"]);
        Assert.Empty(subscription["params"]!["taskLogs"]!.AsArray());
        var emitted = JsonNode.Parse(result.Output.ToString())!;
        Assert.Equal("1.0", emitted["specversion"]!.GetValue<string>());
        Assert.Equal("proj_abc", emitted["projectid"]!.GetValue<string>());
        Assert.Null(emitted["extensions"]);
        Assert.Empty(result.Error.ToString());
    }

    [Fact]
    public async Task Tail_WithMatch_SendsMatchAndSelectedFieldsProjectTheEvent()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory) { OnExhausted = cts.Cancel }
            .AddJson(Ack)
            .AddJson(DomainEvent);
        factory.Add(socket);

        var result = await RunAsync(
            factory,
            cts.Token,
            ["event", "tail", "--match", "event.issue == \"42\"", "--json", "id,type,issue"]);

        Assert.Equal(130, result.ExitCode);
        var subscription = JsonNode.Parse(Assert.Single(socket.SentMessages))!;
        Assert.Equal("event.issue == \"42\"", subscription["params"]!["domain"]!["match"]!.GetValue<string>());
        var emitted = JsonNode.Parse(result.Output.ToString())!.AsObject();
        Assert.Equal(["id", "type", "issue"], emitted.Select(property => property.Key).ToArray());
    }

    [Fact]
    public async Task Tail_WithRepeatedEvents_SendsTrimmedDistinctDomainTypes()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory) { OnExhausted = cts.Cancel }.AddJson(Ack);
        factory.Add(socket);

        var result = await RunAsync(
            factory,
            cts.Token,
            ["event", "tail", "--event", " com.mohist.issue.completed ", "--event", "com.mohist.issue.completed", "--event", "com.mohist.epic.closed"]);

        Assert.Equal(130, result.ExitCode);
        var types = JsonNode.Parse(Assert.Single(socket.SentMessages))!["params"]!["domain"]!["types"]!.AsArray();
        Assert.Equal(["com.mohist.issue.completed", "com.mohist.epic.closed"], types.Select(value => value!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task Tail_EmptyEvent_FailsBeforeProjectResolutionOrSocketCreation()
    {
        var factory = new FakeEventSocketFactory();

        var result = await RunAsync(factory, CancellationToken.None, ["event", "tail", "--event", " "]);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(factory.Endpoints);
        Assert.Empty(result.Handler.Requests);
        Assert.Contains("--event values must not be empty", result.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tail_SubscriptionError_PrintsMatchDiagnosticAndStops()
    {
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory).AddJson(
            """{"jsonrpc":"2.0","id":"req_1","error":{"code":-32602,"message":"Unbalanced '('","data":{"line":1,"column":20,"offset":19,"source":"(event.type"}}}""");
        factory.Add(socket);

        var result = await RunAsync(factory, CancellationToken.None,
            ["event", "tail", "--match", "(event.type == \"x\""]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unbalanced '('", result.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("line 1, column 20", result.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(result.Output.ToString());
        Assert.Equal(WebSocketCloseStatus.NormalClosure, Assert.Single(socket.CloseStatuses));
        Assert.Equal("Subscription rejected.", Assert.Single(socket.CloseDescriptions));
    }

    [Fact]
    public async Task Tail_ReconnectsAfterCloseAndResubscribesAfterInjectedWait()
    {
        using var cts = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var factory = new FakeEventSocketFactory();
        var first = new FakeEventSocket(factory).AddJson(Ack).AddClose(
            WebSocketCloseStatus.EndpointUnavailable,
            "Server restart.");
        var second = new FakeEventSocket(factory) { OnExhausted = cts.Cancel }.AddJson(Ack).AddJson(DomainEvent);
        factory.Add(first);
        factory.Add(second);

        var result = await RunAsync(
            factory,
            cts.Token,
            wait: (delay, _) => { waits.Add(delay); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(2, factory.Endpoints.Count);
        Assert.Single(first.SentMessages);
        Assert.Single(second.SentMessages);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, Assert.Single(first.CloseStatuses));
        Assert.Equal("Server restart.", Assert.Single(first.CloseDescriptions));
        Assert.Single(waits);
        Assert.InRange(waits[0], TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1200));
        Assert.Single(result.Output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Tail_ReconnectsWhenAnEstablishedSocketReceiveFails()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        factory.Add(new FakeEventSocket(factory) { ReceiveException = new WebSocketException() }.AddJson(Ack));
        factory.Add(new FakeEventSocket(factory) { OnExhausted = cts.Cancel }.AddJson(Ack).AddJson(DomainEvent));

        var result = await RunAsync(
            factory,
            cts.Token,
            wait: (_, _) => Task.CompletedTask);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(2, factory.Endpoints.Count);
        Assert.Single(result.Output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Tail_ReresolvesCredentialBeforeReconnect()
    {
        const string firstToken = "first-token-0123456789abcdef0123456789";
        const string secondToken = "second-token-0123456789abcdef0123456789";
        using var cts = new CancellationTokenSource();
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[CliCredentialProvider.TokenEnvironmentVariable] = firstToken;
        var factory = new FakeEventSocketFactory();
        factory.Add(new FakeEventSocket(factory).AddJson(Ack).AddClose());
        factory.Add(new FakeEventSocket(factory) { OnExhausted = cts.Cancel }.AddJson(Ack));

        var result = await RunAsync(
            factory,
            cts.Token,
            environment: environment,
            wait: (_, _) =>
            {
                environment[CliCredentialProvider.TokenEnvironmentVariable] = secondToken;
                return Task.CompletedTask;
            });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal([firstToken, secondToken], factory.BearerTokens);
    }

    [Fact]
    public async Task Tail_StoredSession401_RefreshesPersistsReresolvesAndRetriesOnce()
    {
        const string oldAccess = "moh_session_oldoldoldoldoldoldoldoldoldoldoldold";
        const string newAccess = "moh_session_newnonewnonewnonewnonewnonewnonewn";
        const string newRefresh = "moh_refresh_newnonewnonewnonewnonewnonewnonewn";
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        factory.Add(new FakeEventSocket(factory) { ConnectException = new EventSocketUnauthorizedException() });
        factory.Add(new FakeEventSocket(factory) { OnExhausted = cts.Cancel }.AddJson(Ack));
        var result = await RunAsync(
            factory,
            cts.Token,
            configureFileSystem: fs => fs.AddFile(
                "/mohist-tests/user/.mohist/credentials.json",
                $$"""{"servers":[{"server":"http://localhost:3456","accessToken":"{{oldAccess}}","refreshToken":"moh_refresh_oldoldoldoldoldoldoldoldoldoldoldold","accessExpiresAt":"2025-01-01T00:00:00Z","refreshExpiresAt":"2027-01-01T00:00:00Z"}]}"""),
            responder: (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/auth/token"
                ? RecordingHttpHandler.Json(new { success = true, data = new { accessToken = newAccess, refreshToken = newRefresh, accessExpiresAt = "2027-01-01T00:00:00Z", refreshExpiresAt = "2027-02-01T00:00:00Z" } })
                : RecordingHttpHandler.Json(new { success = true })));

        Assert.Equal(130, result.ExitCode);
        Assert.Equal([oldAccess, newAccess], factory.BearerTokens);
        var stored = JsonNode.Parse(result.FileSystem.ReadAllText("/mohist-tests/user/.mohist/credentials.json"))!;
        Assert.Equal(newRefresh, stored["servers"]![0]!["refreshToken"]!.GetValue<string>());
        Assert.Single(result.Handler.Requests, request => request.RequestUri!.AbsolutePath == "/api/auth/token");
    }

    [Fact]
    public async Task Tail_StoredSession401_WhenPersistenceFails_RetriesWithFreshAccessToken()
    {
        const string oldAccess = "moh_session_oldoldoldoldoldoldoldoldoldoldoldold";
        const string newAccess = "moh_session_newnonewnonewnonewnonewnonewnonewn";
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        factory.Add(new FakeEventSocket(factory) { ConnectException = new EventSocketUnauthorizedException() });
        factory.Add(new FakeEventSocket(factory) { OnExhausted = cts.Cancel }.AddJson(Ack));

        var result = await RunAsync(
            factory,
            cts.Token,
            configureFileSystem: fs =>
            {
                fs.AddFile(
                    "/mohist-tests/user/.mohist/credentials.json",
                    $$"""{"servers":[{"server":"http://localhost:3456","accessToken":"{{oldAccess}}","refreshToken":"moh_refresh_oldoldoldoldoldoldoldoldoldoldoldold","accessExpiresAt":"2025-01-01T00:00:00Z","refreshExpiresAt":"2027-01-01T00:00:00Z"}]}""");
                fs.ThrowOnWriteUserOnly = true;
            },
            responder: (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    accessToken = newAccess,
                    refreshToken = "moh_refresh_newnonewnonewnonewnonewnonewnonewn",
                    accessExpiresAt = "2027-01-01T00:00:00Z",
                    refreshExpiresAt = "2027-02-01T00:00:00Z",
                },
            })));

        Assert.Equal(130, result.ExitCode);
        Assert.Equal([oldAccess, newAccess], factory.BearerTokens);
        Assert.Contains(oldAccess, result.FileSystem.ReadAllText("/mohist-tests/user/.mohist/credentials.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tail_Second401AfterStoredSessionRefreshEntersReconnectBackoff()
    {
        const string oldAccess = "moh_session_oldoldoldoldoldoldoldoldoldoldoldold";
        const string newAccess = "moh_session_newnonewnonewnonewnonewnonewnonewn";
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        factory.Add(new FakeEventSocket(factory) { ConnectException = new EventSocketUnauthorizedException() });
        factory.Add(new FakeEventSocket(factory) { ConnectException = new EventSocketUnauthorizedException() });

        var result = await RunAsync(
            factory,
            cts.Token,
            wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; },
            configureFileSystem: fs => fs.AddFile(
                "/mohist-tests/user/.mohist/credentials.json",
                $$"""{"servers":[{"server":"http://localhost:3456","accessToken":"{{oldAccess}}","refreshToken":"moh_refresh_oldoldoldoldoldoldoldoldoldoldoldold","accessExpiresAt":"2025-01-01T00:00:00Z","refreshExpiresAt":"2027-01-01T00:00:00Z"}]}"""),
            responder: (request, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    accessToken = newAccess,
                    refreshToken = "moh_refresh_newnonewnonewnonewnonewnonewnonewn",
                    accessExpiresAt = "2027-01-01T00:00:00Z",
                    refreshExpiresAt = "2027-02-01T00:00:00Z",
                },
            })));

        Assert.Equal(130, result.ExitCode);
        Assert.Equal([oldAccess, newAccess], factory.BearerTokens);
        Assert.Single(result.Handler.Requests);
    }

    [Fact]
    public async Task Tail_EnvironmentToken401_DoesNotRefreshAndUsesBoundedReconnectWait()
    {
        using var cts = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var factory = new FakeEventSocketFactory();
        factory.Add(new FakeEventSocket(factory) { ConnectException = new EventSocketUnauthorizedException() });
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[CliCredentialProvider.TokenEnvironmentVariable] = "env-token-0123456789abcdef0123456789";

        var result = await RunAsync(
            factory,
            cts.Token,
            environment: environment,
            wait: (delay, _) => { waits.Add(delay); cts.Cancel(); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Single(factory.BearerTokens);
        Assert.Empty(result.Handler.Requests);
        Assert.InRange(Assert.Single(waits), TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1200));
    }

    [Fact]
    public async Task Tail_MachineLocalCredentialIsNotSentToRemoteSocket()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        factory.Add(new FakeEventSocket(factory) { OnExhausted = cts.Cancel }.AddJson(Ack));
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        environment[CliCredentialProvider.AdminTokenEnvironmentVariable] = "admin-token-0123456789abcdef0123456789";

        var result = await RunAsync(factory, cts.Token, environment: environment, baseAddress: "https://mohist.example");

        Assert.Equal(130, result.ExitCode);
        Assert.Null(Assert.Single(factory.BearerTokens));
        Assert.Equal("wss", Assert.Single(factory.Endpoints).Scheme);
    }

    [Fact]
    public async Task Tail_OversizedMessageClosesWith1009BeforeReconnect()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory)
            .AddJson(Ack)
            .AddJson(new string('x', (4 * 1024 * 1024) + 1));
        factory.Add(socket);

        var result = await RunAsync(
            factory,
            cts.Token,
            wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, Assert.Single(socket.CloseStatuses));
        Assert.Empty(result.Output.ToString());
    }

    [Fact]
    public async Task Tail_Cancellation_WhenPeerNeverCompletesClose_AbortsAfterInjectedDeadlineAndReturns130()
    {
        using var cts = new CancellationTokenSource();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory)
        {
            OnExhausted = cts.Cancel,
            OnClose = closeStarted.SetResult,
            CloseNeverCompletes = true,
        }.AddJson(Ack);
        factory.Add(socket);

        var run = RunAsync(factory, cts.Token, timeProvider: time);
        await closeStarted.Task;
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await run;

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, Assert.Single(socket.CloseStatuses));
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Fact]
    public async Task Tail_Oversize_WhenPeerNeverCompletesClose_AbortsAndReconnectsAfterInjectedDeadline()
    {
        using var cts = new CancellationTokenSource();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory)
        {
            OnClose = closeStarted.SetResult,
            CloseNeverCompletes = true,
        }.AddJson(Ack).AddJson(new string('x', (4 * 1024 * 1024) + 1));
        factory.Add(socket);

        var run = RunAsync(
            factory,
            cts.Token,
            wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; },
            timeProvider: time);
        await closeStarted.Task;
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await run;

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, Assert.Single(socket.CloseStatuses));
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Fact]
    public async Task Tail_PeerClose_WhenCloseResponseNeverCompletes_AbortsAndReconnectsAfterInjectedDeadline()
    {
        using var cts = new CancellationTokenSource();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory)
        {
            OnClose = closeStarted.SetResult,
            CloseNeverCompletes = true,
        }.AddJson(Ack).AddClose(WebSocketCloseStatus.EndpointUnavailable, "Maintenance.");
        factory.Add(socket);

        var run = RunAsync(
            factory,
            cts.Token,
            wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; },
            timeProvider: time);
        await closeStarted.Task;
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await run;

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, Assert.Single(socket.CloseStatuses));
        Assert.Equal("Maintenance.", Assert.Single(socket.CloseDescriptions));
        Assert.Equal(1, socket.AbortCount);
        Assert.Equal(1, socket.DisposeCount);
    }

    [Fact]
    public async Task Tail_SubscriptionRejection_WhenCloseNeverCompletes_ReturnsAfterInjectedDeadline()
    {
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory)
        {
            OnClose = closeStarted.SetResult,
            CloseNeverCompletes = true,
        }.AddJson("""{"jsonrpc":"2.0","id":"req_1","error":{"code":-32602,"message":"Rejected"}}""");
        factory.Add(socket);

        var run = RunAsync(factory, CancellationToken.None, timeProvider: time);
        await closeStarted.Task;
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await run;

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, Assert.Single(socket.CloseStatuses));
        Assert.Equal("Subscription rejected.", Assert.Single(socket.CloseDescriptions));
        Assert.Equal(1, socket.AbortCount);
    }

    public static TheoryData<string, string> MalformedSubscriptionResponses => new()
    {
        { "not-json", "not-json" },
        { "array", "[]" },
        { "empty-object", "{}" },
        { "wrong-version", "{\"jsonrpc\":\"1.0\",\"id\":\"req_1\",\"result\":{}}" },
        { "numeric-id", "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}" },
        { "wrong-id", "{\"jsonrpc\":\"2.0\",\"id\":\"other\",\"result\":{}}" },
        { "missing-outcome", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\"}" },
        { "result-and-error", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"result\":{},\"error\":{}}" },
        { "array-result", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"result\":[]}" },
        { "response-method", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"result\":{},\"method\":\"event.domain\"}" },
        { "response-params", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"result\":{},\"params\":{}}" },
        { "extra-response-member", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"result\":{},\"extra\":true}" },
        { "duplicate-version", "{\"jsonrpc\":\"2.0\",\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"result\":{}}" },
        { "empty-error", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"error\":{}}" },
        { "non-string-message", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"error\":{\"code\":-32602,\"message\":1}}" },
        { "non-integer-code", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"error\":{\"code\":-32602.5,\"message\":\"Rejected\"}}" },
        { "extra-error-member", "{\"jsonrpc\":\"2.0\",\"id\":\"req_1\",\"error\":{\"code\":-32602,\"message\":\"Rejected\",\"other\":true}}" },
    };

    [Theory]
    [MemberData(nameof(MalformedSubscriptionResponses))]
    public async Task Tail_MalformedSubscriptionResponse_FencesAttemptWithoutOutput(string caseName, string response)
    {
        _ = caseName;
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory).AddJson(response);
        factory.Add(socket);

        var result = await RunAsync(factory, cts.Token, wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, Assert.Single(socket.CloseStatuses));
        Assert.Equal(1, socket.AbortCount);
        Assert.Empty(result.Output.ToString());
    }

    [Theory]
    [InlineData("event-not-object", "{\"jsonrpc\":\"2.0\",\"method\":\"event.domain\",\"params\":{\"event\":[]}}")]
    [InlineData("params-not-object", "{\"jsonrpc\":\"2.0\",\"method\":\"event.domain\",\"params\":[]}")]
    [InlineData("method-not-string", "{\"jsonrpc\":\"2.0\",\"method\":1,\"params\":{}}")]
    [InlineData("missing-params", "{\"jsonrpc\":\"2.0\",\"method\":\"event.domain\"}")]
    [InlineData("response-id", "{\"jsonrpc\":\"2.0\",\"method\":\"event.domain\",\"params\":{},\"id\":\"x\"}")]
    [InlineData("response-result", "{\"jsonrpc\":\"2.0\",\"method\":\"event.domain\",\"params\":{},\"result\":{}}")]
    [InlineData("response-error", "{\"jsonrpc\":\"2.0\",\"method\":\"event.domain\",\"params\":{},\"error\":{}}")]
    [InlineData("extra-envelope-member", "{\"jsonrpc\":\"2.0\",\"method\":\"unknown\",\"params\":{},\"extra\":true}")]
    [InlineData("extra-domain-param", "{\"jsonrpc\":\"2.0\",\"method\":\"event.domain\",\"params\":{\"event\":{},\"other\":true}}")]
    public async Task Tail_MalformedNotification_FencesAttemptWithoutOutput(string caseName, string notification)
    {
        _ = caseName;
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory).AddJson(Ack).AddJson(notification);
        factory.Add(socket);

        var result = await RunAsync(factory, cts.Token, wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, Assert.Single(socket.CloseStatuses));
        Assert.Empty(result.Output.ToString());
    }

    public static TheoryData<string, string> MalformedCloudEvents => new()
    {
        { "empty", "{}" },
        { "wrong-specversion", "{\"specversion\":\"0.3\",\"id\":\"e1\",\"source\":\"test\",\"type\":\"one\"}" },
        { "empty-id", "{\"specversion\":\"1.0\",\"id\":\"\",\"source\":\"test\",\"type\":\"one\"}" },
        { "non-string-source", "{\"specversion\":\"1.0\",\"id\":\"e1\",\"source\":1,\"type\":\"one\"}" },
        { "missing-type", "{\"specversion\":\"1.0\",\"id\":\"e1\",\"source\":\"test\"}" },
        { "duplicate-type", "{\"specversion\":\"1.0\",\"id\":\"e1\",\"source\":\"test\",\"type\":\"one\",\"type\":\"two\"}" },
    };

    [Theory]
    [MemberData(nameof(MalformedCloudEvents))]
    public async Task Tail_MalformedCloudEvent_FencesAttemptWithoutOutput(string caseName, string cloudEvent)
    {
        _ = caseName;
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var notification = """{"jsonrpc":"2.0","method":"event.domain","params":{"event":"""
            + cloudEvent
            + "}}";
        var socket = new FakeEventSocket(factory).AddJson(Ack).AddJson(notification);
        factory.Add(socket);

        var result = await RunAsync(factory, cts.Token, wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, Assert.Single(socket.CloseStatuses));
        Assert.Empty(result.Output.ToString());
    }

    [Fact]
    public async Task Tail_PrettyPrintedFragmentedCloudEvent_EmitsOneCompactNdjsonLine()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        const string first = """
            {
              "jsonrpc": "2.0",
              "method": "event.domain",
              "params": {
                "event": {
                  "specversion": "1.0",
            """;
        const string second = """
                  "id": "e1",
                  "source": "test",
                  "type": "one",
                  "data": { "value": 1 }
                }
              }
            }
            """;
        var socket = new FakeEventSocket(factory) { OnExhausted = cts.Cancel }
            .AddJson(Ack)
            .AddFragment(first, false)
            .AddFragment(second, true);
        factory.Add(socket);

        var result = await RunAsync(factory, cts.Token);

        Assert.Equal(130, result.ExitCode);
        var lines = result.Output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("{\"specversion\":\"1.0\",\"id\":\"e1\",\"source\":\"test\",\"type\":\"one\",\"data\":{\"value\":1}}", Assert.Single(lines).TrimEnd('\r'));
    }

    [Fact]
    public async Task Tail_BinaryMessage_FencesAttemptWithInvalidMessageType()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory).AddJson(Ack).AddBinary("binary");
        factory.Add(socket);

        var result = await RunAsync(factory, cts.Token, wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, Assert.Single(socket.CloseStatuses));
        Assert.Empty(result.Output.ToString());
    }

    [Fact]
    public async Task Tail_FragmentedMessage_EnforcesAggregateSizeLimit()
    {
        using var cts = new CancellationTokenSource();
        var factory = new FakeEventSocketFactory();
        var socket = new FakeEventSocket(factory)
            .AddJson(Ack)
            .AddFragment(new string('x', 2 * 1024 * 1024), false)
            .AddFragment(new string('x', (2 * 1024 * 1024) + 1), true);
        factory.Add(socket);

        var result = await RunAsync(factory, cts.Token, wait: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, Assert.Single(socket.CloseStatuses));
        Assert.Empty(result.Output.ToString());
    }

    [Fact]
    public async Task Tail_NoActiveProject_FailsWithoutSocketOrHttp()
    {
        var factory = new FakeEventSocketFactory();
        var result = await RunAsync(factory, CancellationToken.None, activeProjectId: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(factory.Endpoints);
        Assert.Empty(result.Handler.Requests);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, result.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tail_PluralNoun_DoesNotResolveWithoutSocket()
    {
        var factory = new FakeEventSocketFactory();
        var result = await RunAsync(factory, CancellationToken.None, ["events", "tail"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(factory.Endpoints);
    }

    [Fact]
    public async Task Tail_Help_DescribesPostSubscriptionNdjson()
    {
        var factory = new FakeEventSocketFactory();
        var result = await RunAsync(factory, CancellationToken.None, ["event", "tail", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("subscription establishment", result.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("NDJSON", result.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--event", result.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("repeatable", result.Output.ToString(), StringComparison.Ordinal);
        Assert.Empty(factory.Endpoints);
    }

    [Fact]
    public async Task Tail_JsonDiscovery_IncludesAllCanonicalLineageExtensions()
    {
        var factory = new FakeEventSocketFactory();

        var result = await RunAsync(factory, CancellationToken.None, ["event", "tail", "--json"]);

        Assert.Equal(0, result.ExitCode);
        var fields = JsonNode.Parse(result.Output.ToString())!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Contains("parent", fields);
        Assert.Contains("githubrepo", fields);
        Assert.Contains("githubissue", fields);
        Assert.Empty(factory.Endpoints);
    }

    private static async Task<TailResult> RunAsync(
        FakeEventSocketFactory factory,
        CancellationToken cancellationToken,
        string[]? args = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        IEnvironmentVariableProvider? environment = null,
        string? activeProjectId = "proj_abc",
        string baseAddress = CliTestFactory.BaseAddress,
        Action<FakeFileSystem>? configureFileSystem = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null,
        TimeProvider? timeProvider = null)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(responder, activeProjectId);
        http.BaseAddress = new Uri(baseAddress);
        configureFileSystem?.Invoke(fs);
        var exitCode = await MohistCliCommands.RunAsync(
            http,
            args ?? ["event", "tail"],
            output,
            error,
            fs,
            executor,
            environment,
            cancellationToken: cancellationToken,
            timeProvider: timeProvider,
            pollWait: wait ?? WaitForCancellationAsync,
            eventSocketFactory: factory,
            eventReconnectJitter: () => 0.5);
        return new TailResult(exitCode, handler, output, error, fs);
    }

    private static async Task WaitForCancellationAsync(TimeSpan _, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(cancelled.SetResult);
        await cancelled.Task;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed record TailResult(
        int ExitCode,
        RecordingHttpHandler Handler,
        StringWriter Output,
        StringWriter Error,
        FakeFileSystem FileSystem);
}
