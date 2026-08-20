using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Api;
using Mohist.Server.Events.WebSocket;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;
using Mohist.Server.Webhooks;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public sealed class EventWebSocketConnectionTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task NonObjectJsonIsInvalidRequestAndCountsTowardPolicyClose(string json)
    {
        await using var fixture = new ConnectionFixture();
        for (var index = 0; index < 3; index++)
        {
            fixture.Socket.ReceiveText(json);
            var error = await fixture.NextJsonAsync();
            Assert.Equal(JsonValueKind.Null, error.GetProperty("id").ValueKind);
            Assert.Equal(-32600, error.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.Equal(WebSocketCloseStatus.PolicyViolation,
            (await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task BatchIsOneInvalidRequestRatherThanIdlessNotification()
    {
        await using var fixture = new ConnectionFixture();
        fixture.Socket.ReceiveText("""[{"jsonrpc":"2.0","id":"set","method":"subscription.set","params":{}}]""");

        var error = await fixture.NextJsonAsync();
        Assert.Equal(JsonValueKind.Null, error.GetProperty("id").ValueKind);
        Assert.Equal(-32600, error.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task CompleteSubscriptionPublishesAllThreeNotificationShapes()
    {
        await using var fixture = new ConnectionFixture();
        fixture.SendSubscription("set-1");
        Assert.Equal("set-1", Id(await fixture.NextJsonAsync()));

        var cloudEvent = CloudEventFor("project-a", EventCatalog.ReverseDns.IssueCompleted);
        var rendered = JsonSerializer.Deserialize<JsonElement>(new WebhookPayloadRenderer().Render(cloudEvent));
        fixture.Connection.TryPublishDomain(cloudEvent, DomainPayload(rendered));
        fixture.PublishTranscript(Transcript("message.delta"));
        fixture.PublishTaskLog(TaskLog("project-a", "run-1", "task-1"));

        var domain = await fixture.NextJsonAsync();
        Assert.Equal("event.domain", domain.GetProperty("method").GetString());
        Assert.True(JsonElement.DeepEquals(rendered, domain.GetProperty("params").GetProperty("event")));
        Assert.Equal("event.transcript", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
        Assert.Equal("event.task-log", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
    }

    [Fact]
    public async Task InvalidReplacementLeavesPriorSubscriptionActive()
    {
        await using var fixture = new ConnectionFixture();
        fixture.SendSubscription("set-1");
        await fixture.NextJsonAsync();
        fixture.Socket.ReceiveText("""
            {"jsonrpc":"2.0","id":"set-2","method":"subscription.set","params":{"domain":null,"transcript":{"types":["unknown"]},"taskLogs":[]}}
            """);
        var error = await fixture.NextJsonAsync();
        Assert.Equal(-32602, error.GetProperty("error").GetProperty("code").GetInt32());

        fixture.PublishTranscript(Transcript("message.delta"));
        Assert.Equal("event.transcript", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnknownDomainTypesAreNormalizedAndWhitespaceReplacementIsAtomic(string invalidType)
    {
        const string futureType = "com.example.future.event";
        var installed = new List<EventSubscription>();
        await using var fixture = new ConnectionFixture(subscriptionChanged: installed.Add);
        fixture.Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "future",
            method = "subscription.set",
            @params = new
            {
                domain = new { types = new[] { $"  {futureType}  ", futureType }, match = (string?)null },
                transcript = (object?)null,
                taskLogs = Array.Empty<object>(),
            },
        }));

        Assert.Equal("future", Id(await fixture.NextJsonAsync()));
        Assert.Equal(futureType, Assert.Single(Assert.Single(installed).DomainTypes!));

        fixture.Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "invalid",
            method = "subscription.set",
            @params = new
            {
                domain = new { types = new[] { invalidType }, match = (string?)null },
                transcript = (object?)null,
                taskLogs = Array.Empty<object>(),
            },
        }));
        Assert.Equal(-32602, (await fixture.NextJsonAsync()).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Single(installed);

        var rendered = JsonSerializer.SerializeToElement(new { });
        fixture.Connection.TryPublishDomain(CloudEventFor("project-a", futureType), DomainPayload(rendered));
        Assert.Equal("event.domain", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
    }

    [Theory]
    [InlineData("root", -32600)]
    [InlineData("params", -32602)]
    [InlineData("domain", -32602)]
    [InlineData("transcript", -32602)]
    [InlineData("taskLog", -32602)]
    public async Task UnknownPropertiesAreRejectedWithoutChangingState(string scope, int code)
    {
        await using var fixture = new ConnectionFixture();
        fixture.SendSubscription("initial");
        await fixture.NextJsonAsync();
        fixture.Socket.ReceiveText(SubscriptionWithUnknownProperty(scope));

        Assert.Equal(code, (await fixture.NextJsonAsync()).GetProperty("error").GetProperty("code").GetInt32());
        fixture.PublishTranscript(Transcript("message.delta"));
        Assert.Equal("event.transcript", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
    }

    [Fact]
    public async Task OversizedMatchAndTaskScopeLimitLeavePriorStateActive()
    {
        await using var fixture = new ConnectionFixture();
        fixture.SendSubscription("initial");
        await fixture.NextJsonAsync();

        fixture.Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "large-match",
            method = "subscription.set",
            @params = new
            {
                domain = new { types = (string[]?)null, match = new string('x', 8193) },
                transcript = (object?)null,
                taskLogs = Array.Empty<object>(),
            },
        }));
        Assert.Equal(-32602, (await fixture.NextJsonAsync()).GetProperty("error").GetProperty("code").GetInt32());

        fixture.Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "many-scopes",
            method = "subscription.set",
            @params = new
            {
                domain = (object?)null,
                transcript = (object?)null,
                taskLogs = Enumerable.Range(0, 129).Select(index => new { workflowRunId = $"run-{index}", taskId = $"task-{index}" }),
            },
        }));
        Assert.Equal(-32602, (await fixture.NextJsonAsync()).GetProperty("error").GetProperty("code").GetInt32());

        fixture.PublishTranscript(Transcript("message.delta"));
        Assert.Equal("event.transcript", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
    }

    [Fact]
    public async Task MatchAndExactTypeMustBothMatch()
    {
        await using var fixture = new ConnectionFixture();
        fixture.Socket.ReceiveText("""
            {"jsonrpc":"2.0","id":"set","method":"subscription.set","params":{"domain":{"types":["com.mohist.issue.completed"],"match":"event.issue == \"42\""},"transcript":null,"taskLogs":[]}}
            """);
        await fixture.NextJsonAsync();

        var structured = JsonSerializer.SerializeToElement(new { });
        fixture.Connection.TryPublishDomain(CloudEventFor("project-a", EventCatalog.ReverseDns.IssueCompleted, "41"), DomainPayload(structured));
        fixture.Connection.TryPublishDomain(CloudEventFor("project-a", EventCatalog.ReverseDns.IssueCreated, "42"), DomainPayload(structured));
        fixture.Connection.TryPublishDomain(CloudEventFor("project-a", EventCatalog.ReverseDns.IssueCompleted, "42"), DomainPayload(structured));

        Assert.Equal("event.domain", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
        Assert.Equal(2, fixture.Socket.SentCount);
    }

    [Fact]
    public async Task DuplicateValuesNormalizeAndWildcardAndTaskScopesFilterExactly()
    {
        await using var fixture = new ConnectionFixture();
        fixture.Socket.ReceiveText("""
            {"jsonrpc":"2.0","id":"set","method":"subscription.set","params":{"domain":{"types":null,"match":null},"transcript":{"types":["message.delta","message.delta"]},"taskLogs":[{"workflowRunId":"run-1","taskId":"task-1"},{"workflowRunId":"run-1","taskId":"task-1"}]}}
            """);
        await fixture.NextJsonAsync();

        fixture.Connection.TryPublishDomain(
            CloudEventFor("project-a", EventCatalog.ReverseDns.IssueCreated),
            DomainPayload(JsonSerializer.SerializeToElement(new { })));
        fixture.PublishTranscript(Transcript("message.delta"));
        fixture.PublishTaskLog(TaskLog("project-a", "run-1", "task-2"));
        fixture.PublishTaskLog(TaskLog("project-a", "run-1", "task-1"));

        Assert.Equal("event.domain", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
        Assert.Equal("event.transcript", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
        Assert.Equal("event.task-log", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
        Assert.Equal(4, fixture.Socket.SentCount);
    }

    [Fact]
    public async Task MatchRuntimeFailureUsesInjectedSink()
    {
        var failures = new RecordingMatchFailureSink();
        await using var fixture = new ConnectionFixture(failures, TimeSpan.FromTicks(1));
        fixture.Socket.ReceiveText("""
            {"jsonrpc":"2.0","id":"set","method":"subscription.set","params":{"domain":{"types":null,"match":"event.type.matches(\"^(a+)+$\")"},"transcript":null,"taskLogs":[]}}
            """);
        await fixture.NextJsonAsync();

        var cloudEvent = CloudEventFor("project-a", new string('a', 1000) + "!");
        fixture.Connection.TryPublishDomain(cloudEvent, DomainPayload(JsonSerializer.SerializeToElement(new { })));

        var failure = Assert.Single(failures.Failures);
        Assert.Equal("event.type.matches(\"^(a+)+$\")", failure.Source);
        Assert.IsType<System.Text.RegularExpressions.RegexMatchTimeoutException>(failure.Exception);
        Assert.Equal(1, fixture.Socket.SentCount);
    }

    [Fact]
    public async Task InvalidExpressionReturnsLocationDiagnostic()
    {
        await using var fixture = new ConnectionFixture();
        fixture.Socket.ReceiveText("""
            {"jsonrpc":"2.0","id":"set","method":"subscription.set","params":{"domain":{"types":null,"match":"event.type =="},"transcript":null,"taskLogs":[]}}
            """);

        var error = (await fixture.NextJsonAsync()).GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("event.type ==", data.GetProperty("source").GetString());
        Assert.True(data.GetProperty("line").GetInt32() > 0);
        Assert.True(data.GetProperty("column").GetInt32() > 0);
    }

    [Fact]
    public async Task ThirdProtocolErrorSendsResponseBeforePolicyClose()
    {
        await using var fixture = new ConnectionFixture();
        for (var index = 0; index < 3; index++)
        {
            fixture.Socket.ReceiveText("{");
            Assert.Equal(-32700, (await fixture.NextJsonAsync()).GetProperty("error").GetProperty("code").GetInt32());
        }

        var close = await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.Status);
        Assert.Equal(3, fixture.Socket.SentCount);
    }

    [Fact]
    public async Task IdlessCallsAreIgnoredAndThirdCloses()
    {
        await using var fixture = new ConnectionFixture();
        for (var index = 0; index < 3; index++)
            fixture.Socket.ReceiveText("""{"jsonrpc":"2.0","method":"subscription.set","params":{}}""");

        var close = await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.Status);
        Assert.Equal(0, fixture.Socket.SentCount);
    }

    [Fact]
    public async Task BinaryMessagesAreRejectedAndCountTowardPolicyClose()
    {
        await using var fixture = new ConnectionFixture();
        for (var index = 0; index < 3; index++)
        {
            fixture.Socket.ReceiveBinary([1, 2, 3], true);
            Assert.Equal(-32600, (await fixture.NextJsonAsync()).GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.Equal(WebSocketCloseStatus.PolicyViolation,
            (await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task FragmentedMultibyteUtf8IsAcceptedAndInvalidUtf8IsParseError()
    {
        await using var fixture = new ConnectionFixture();
        var request = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"réq","method":"subscription.set","params":{"domain":null,"transcript":null,"taskLogs":[]}}""");
        var split = Array.IndexOf(request, (byte)0xC3) + 1;
        fixture.Socket.ReceiveText(request[..split], false);
        fixture.Socket.ReceiveText(request[split..], true);
        Assert.Equal("réq", Id(await fixture.NextJsonAsync()));

        fixture.Socket.ReceiveText([0xC3, 0x28], true);
        Assert.Equal(-32700, (await fixture.NextJsonAsync()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SuccessfulResponseIsQueuedBeforeNotificationAdmittedByNewSubscription()
    {
        var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ConnectionFixture(subscriptionChanged: _ =>
        {
            installed.TrySetResult();
            publishAttempted.Task.Wait(TestContext.Current.CancellationToken);
        });
        var publish = Task.Run(async () =>
        {
            await installed.Task.WaitAsync(TestContext.Current.CancellationToken);
            publishAttempted.TrySetResult();
            fixture.PublishTranscript(Transcript("message.delta"));
        }, TestContext.Current.CancellationToken);

        fixture.SendSubscription("set");
        Assert.Equal("set", Id(await fixture.NextJsonAsync()));
        Assert.Equal("event.transcript", (await fixture.NextJsonAsync()).GetProperty("method").GetString());
        await publish;
    }

    [Fact]
    public async Task OversizedFragmentedInputClosesWith1009()
    {
        await using var fixture = new ConnectionFixture();
        for (var index = 0; index < 256; index++)
            fixture.Socket.ReceiveText(new byte[16 * 1024], endOfMessage: false);
        fixture.Socket.ReceiveText([0], endOfMessage: true);

        Assert.Equal(WebSocketCloseStatus.MessageTooBig,
            (await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task SharedOutgoingQueueSaturationClosesWith1013()
    {
        await using var fixture = new ConnectionFixture();
        fixture.SendSubscription("set");
        await fixture.NextJsonAsync();
        fixture.Socket.PauseSends();
        fixture.PublishTranscript(Transcript("message.delta"));
        await fixture.Socket.SendStarted.WaitAsync(TestContext.Current.CancellationToken);
        for (var index = 0; index < 256; index++)
            fixture.PublishTranscript(Transcript("message.delta"));
        fixture.PublishTranscript(Transcript("message.delta"));
        fixture.Socket.ReleaseSends();

        Assert.Equal((WebSocketCloseStatus)1013,
            (await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task SendFailureCompletesRunAndRemovesRegistryConnection()
    {
        var registry = new EventWebSocketRegistry(
            new WebhookPayloadRenderer(),
            new FakeTimeProvider(),
            NullLoggerFactory.Instance,
            NullEventMatchFailureSink.Instance);
        using var socket = new FakeWebSocket();
        socket.FailNextSend(new IOException("connection reset"));

        var run = registry.RunAsync("project-a", socket, TestContext.Current.CancellationToken);
        socket.ReceiveText(SubscriptionJson("set"));

        await run;
        Assert.Equal(WebSocketCloseStatus.NormalClosure,
            (await socket.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
        await registry.PublishTranscriptAsync("project-a", Transcript("message.delta"));
        Assert.Equal(0, socket.SentCount);
    }

    [Fact]
    public async Task OversizedNotificationClosesWith1009()
    {
        await using var fixture = new ConnectionFixture();
        fixture.SendSubscription("set");
        await fixture.NextJsonAsync();
        var oversized = new TranscriptEnvelope(
            "part", "session", null, null, 1, "message.delta",
            JsonSerializer.SerializeToElement(new string('x', 4 * 1024 * 1024)),
            "2026-08-20T12:00:00.0000000+00:00");

        fixture.PublishTranscript(oversized);

        Assert.Equal(WebSocketCloseStatus.MessageTooBig,
            (await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task RegistryRoutesOnlyToTheExactPublicationProject()
    {
        var time = new FakeTimeProvider();
        var registry = new EventWebSocketRegistry(
            new WebhookPayloadRenderer(), time, NullLoggerFactory.Instance, NullEventMatchFailureSink.Instance);
        using var stop = new CancellationTokenSource();
        using var socketA = new FakeWebSocket();
        using var socketB = new FakeWebSocket();
        var runA = registry.RunAsync("project-a", socketA, stop.Token);
        var runB = registry.RunAsync("project-b", socketB, stop.Token);
        socketA.ReceiveText(SubscriptionJson("a"));
        socketB.ReceiveText(SubscriptionJson("b"));
        await socketA.NextSentAsync(TestContext.Current.CancellationToken);
        await socketB.NextSentAsync(TestContext.Current.CancellationToken);

        await registry.PublishDomainAsync(CloudEventFor("project-a", EventCatalog.ReverseDns.IssueCompleted));

        var notification = JsonDocument.Parse(await socketA.NextSentAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("event.domain", notification.GetProperty("method").GetString());
        Assert.Equal(1, socketB.SentCount);

        await registry.PublishTranscriptAsync("project-b", Transcript("message.delta"));
        Assert.Equal("event.transcript",
            JsonDocument.Parse(await socketB.NextSentAsync(TestContext.Current.CancellationToken)).RootElement
                .GetProperty("method").GetString());
        Assert.Equal(2, socketA.SentCount);

        await registry.PublishTaskLogAsync(TaskLog("project-a", "run-1", "task-1"));
        Assert.Equal("event.task-log",
            JsonDocument.Parse(await socketA.NextSentAsync(TestContext.Current.CancellationToken)).RootElement
                .GetProperty("method").GetString());
        Assert.Equal(2, socketB.SentCount);

        stop.Cancel();
        await Task.WhenAll(IgnoreCancellation(runA), IgnoreCancellation(runB));
    }

    [Fact]
    public async Task DisconnectingLastConnectionThenReconnectCannotDetachProjectBucket()
    {
        var registry = new EventWebSocketRegistry(
            new WebhookPayloadRenderer(),
            new FakeTimeProvider(),
            NullLoggerFactory.Instance,
            NullEventMatchFailureSink.Instance);
        using var first = new FakeWebSocket();
        var firstRun = registry.RunAsync("project-a", first, TestContext.Current.CancellationToken);
        first.ReceiveClose();
        await firstRun;

        using var second = new FakeWebSocket();
        var secondRun = registry.RunAsync("project-a", second, TestContext.Current.CancellationToken);
        second.ReceiveText(SubscriptionJson("second"));
        await second.NextSentAsync(TestContext.Current.CancellationToken);

        await registry.PublishTranscriptAsync("project-a", Transcript("message.delta"));
        Assert.Equal("event.transcript",
            JsonDocument.Parse(await second.NextSentAsync(TestContext.Current.CancellationToken)).RootElement
                .GetProperty("method").GetString());

        second.ReceiveClose();
        await secondRun;
    }

    [Fact]
    public async Task AllPublisherKindsRequireExactNonBlankProjectMetadata()
    {
        var registry = new EventWebSocketRegistry(
            new WebhookPayloadRenderer(),
            new FakeTimeProvider(),
            NullLoggerFactory.Instance,
            NullEventMatchFailureSink.Instance);
        using var socket = new FakeWebSocket();
        var run = registry.RunAsync("project-a", socket, TestContext.Current.CancellationToken);
        socket.ReceiveText(SubscriptionJson("set"));
        await socket.NextSentAsync(TestContext.Current.CancellationToken);

        await registry.PublishDomainAsync(CloudEventWithoutProject());
        await registry.PublishDomainAsync(CloudEventFor("project-b", EventCatalog.ReverseDns.IssueCompleted));
        await registry.PublishTranscriptAsync("", Transcript("message.delta"));
        await registry.PublishTranscriptAsync("project-b", Transcript("message.delta"));
        await registry.PublishTaskLogAsync(TaskLog("", "run-1", "task-1"));
        await registry.PublishTaskLogAsync(TaskLog("project-b", "run-1", "task-1"));
        Assert.Equal(1, socket.SentCount);

        await registry.PublishDomainAsync(CloudEventFor("project-a", EventCatalog.ReverseDns.IssueCompleted));
        await registry.PublishTranscriptAsync("project-a", Transcript("message.delta"));
        await registry.PublishTaskLogAsync(TaskLog("project-a", "run-1", "task-1"));
        Assert.Equal("event.domain", JsonDocument.Parse(await socket.NextSentAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("method").GetString());
        Assert.Equal("event.transcript", JsonDocument.Parse(await socket.NextSentAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("method").GetString());
        Assert.Equal("event.task-log", JsonDocument.Parse(await socket.NextSentAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("method").GetString());

        socket.ReceiveClose();
        await run;
    }

    [Fact]
    public async Task StopFencesActiveConnectionsAndLatePublicationCannotReviveThem()
    {
        var registry = new EventWebSocketRegistry(
            new WebhookPayloadRenderer(),
            new FakeTimeProvider(),
            NullLoggerFactory.Instance,
            NullEventMatchFailureSink.Instance);
        using var socket = new FakeWebSocket();
        var run = registry.RunAsync("project-a", socket, TestContext.Current.CancellationToken);
        socket.ReceiveText(SubscriptionJson("set"));
        await socket.NextSentAsync(TestContext.Current.CancellationToken);

        await registry.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable,
            (await socket.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
        await registry.PublishTranscriptAsync("project-a", Transcript("message.delta"));
        Assert.Equal(1, socket.SentCount);
        await run;
    }

    [Fact]
    public async Task AdmissionConcurrentWithStopIsRejectedOutsideRegistry()
    {
        var registry = new EventWebSocketRegistry(
            new WebhookPayloadRenderer(),
            new FakeTimeProvider(),
            NullLoggerFactory.Instance,
            NullEventMatchFailureSink.Instance);
        using var active = new FakeWebSocket();
        var activeRun = registry.RunAsync("project-a", active, TestContext.Current.CancellationToken);
        active.ReceiveText(SubscriptionJson("active"));
        await active.NextSentAsync(TestContext.Current.CancellationToken);
        active.PauseCloses();

        var stop = registry.StopAsync(TestContext.Current.CancellationToken);
        await active.CloseStarted.WaitAsync(TestContext.Current.CancellationToken);

        using var concurrent = new FakeWebSocket();
        var concurrentRun = registry.RunAsync("project-a", concurrent, TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable,
            (await concurrent.Closed.WaitAsync(TestContext.Current.CancellationToken)).Status);
        await concurrentRun;
        Assert.False(stop.IsCompleted);

        concurrent.ReceiveText(SubscriptionJson("concurrent"));
        await registry.PublishTranscriptAsync("project-a", Transcript("message.delta"));
        Assert.Equal(0, concurrent.SentCount);

        active.ReleaseCloses();
        await stop;
        await activeRun;
    }

    [Fact]
    public async Task AdmissionAfterStopUsesBoundedCloseThenAborts()
    {
        var time = new FakeTimeProvider();
        var registry = new EventWebSocketRegistry(
            new WebhookPayloadRenderer(),
            time,
            NullLoggerFactory.Instance,
            NullEventMatchFailureSink.Instance);
        await registry.StopAsync(TestContext.Current.CancellationToken);
        using var socket = new FakeWebSocket();
        socket.PauseCloses();

        var run = registry.RunAsync("project-a", socket, TestContext.Current.CancellationToken);
        await socket.CloseStarted.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));

        await run;
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.Equal(0, socket.SentCount);
    }

    [Theory]
    [InlineData("http", "example.test", "http://example.test", "127.0.0.1", null, null, true)]
    [InlineData("http", "example.test", null, "127.0.0.1", null, null, false)]
    [InlineData("http", "internal:3456", "https://mohist.test", "127.0.0.1", "https", "mohist.test", true)]
    [InlineData("http", "internal:3456", "https://mohist.test", "127.0.0.1", "https,http", "mohist.test", false)]
    [InlineData("http", "internal:3456", "https://mohist.test", "127.0.0.1", "https", null, false)]
    [InlineData("http", "internal:3456", "https://mohist.test", "192.0.2.1", "https", "mohist.test", false)]
    [InlineData("http", "internal:3456", "http://internal:3456", "192.0.2.1", "https", "mohist.test", true)]
    public void CookieOriginUsesOnlyValidatedLoopbackForwardedPair(
        string scheme,
        string host,
        string? origin,
        string remote,
        string? forwardedProto,
        string? forwardedHost,
        bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = HostString.FromUriComponent(host);
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (forwardedProto is not null) context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        if (forwardedHost is not null) context.Request.Headers["X-Forwarded-Host"] = forwardedHost;

        Assert.Equal(expected, ProjectEventSocketRoutes.HasValidOrigin(context.Request, context.Connection.RemoteIpAddress));
    }

    private static CloudEvent CloudEventFor(string projectId, string type, string issue = "42") => new(
        "evt-1",
        new Uri($"/mohist/projects/{projectId}/issues/{issue}", UriKind.Relative),
        type,
        new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
        JsonSerializer.SerializeToElement(new { }),
        extensions: new Dictionary<string, string>
        {
            [EventCatalog.Lineage.ProjectId] = projectId,
            [EventCatalog.Lineage.Issue] = issue,
        });

    private static CloudEvent CloudEventWithoutProject() => new(
        "evt-unprojected",
        new Uri("/mohist/orphan", UriKind.Relative),
        EventCatalog.ReverseDns.IssueCompleted,
        new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
        JsonSerializer.SerializeToElement(new { }));

    private static TranscriptEnvelope Transcript(string type) => new(
        "part-1", "session-1", "runtime-1", "opencode", 1, type,
        JsonSerializer.SerializeToElement(new { text = "hello" }), "2026-08-20T12:00:00.0000000+00:00");

    private static TaskLogDeltaEnvelope TaskLog(string projectId, string runId, string taskId) => new(
        "workflow", runId, projectId, "work-1", taskId,
        [new(1, new DateTimeOffset(2026, 8, 20, 12, 0, 1, TimeSpan.Zero), "stdout", "done")], false);

    private static string? Id(JsonElement element) => element.GetProperty("id").GetString();

    private static byte[] DomainPayload(JsonElement structuredEvent) =>
        EventWebSocketRegistry.SerializeNotification("event.domain", new { @event = structuredEvent });

    private static string SubscriptionJson(string id) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id,
        method = "subscription.set",
        @params = new
        {
            domain = new { types = new[] { EventCatalog.ReverseDns.IssueCompleted }, match = (string?)null },
            transcript = new { types = new[] { "message.delta" } },
            taskLogs = new[] { new { workflowRunId = "run-1", taskId = "task-1" } },
        },
    });

    private static string SubscriptionWithUnknownProperty(string scope) => scope switch
    {
        "root" => """{"jsonrpc":"2.0","id":"invalid","method":"subscription.set","params":{"domain":null,"transcript":null,"taskLogs":[]},"projectId":"project-a"}""",
        "params" => """{"jsonrpc":"2.0","id":"invalid","method":"subscription.set","params":{"domain":null,"transcript":null,"taskLogs":[],"projectRef":"project-a"}}""",
        "domain" => """{"jsonrpc":"2.0","id":"invalid","method":"subscription.set","params":{"domain":{"types":null,"match":null,"projectId":"project-a"},"transcript":null,"taskLogs":[]}}""",
        "transcript" => """{"jsonrpc":"2.0","id":"invalid","method":"subscription.set","params":{"domain":null,"transcript":{"types":["message.delta"],"projectRef":"project-a"},"taskLogs":[]}}""",
        "taskLog" => """{"jsonrpc":"2.0","id":"invalid","method":"subscription.set","params":{"domain":null,"transcript":null,"taskLogs":[{"workflowRunId":"run-1","taskId":"task-1","projectId":"project-a"}]}}""",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    private sealed class ConnectionFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public ConnectionFixture(
            IEventMatchFailureSink? matchFailures = null,
            TimeSpan? matchRegexTimeout = null,
            Action<EventSubscription>? subscriptionChanged = null)
        {
            Socket = new FakeWebSocket();
            Connection = new EventWebSocketConnection(
                "project-a",
                Socket,
                new FakeTimeProvider(),
                subscriptionChanged ?? (_ => { }),
                NullLogger.Instance,
                matchFailures ?? NullEventMatchFailureSink.Instance,
                matchRegexTimeout);
            _run = Connection.RunAsync(_stop.Token);
        }

        public FakeWebSocket Socket { get; }
        public EventWebSocketConnection Connection { get; }

        public void PublishTranscript(TranscriptEnvelope envelope) => Connection.TryPublishTranscript(
            envelope.Type,
            EventWebSocketRegistry.SerializeNotification("event.transcript", new { @event = envelope }));

        public void PublishTaskLog(TaskLogDeltaEnvelope envelope) => Connection.TryPublishTaskLog(
            envelope.OwnerId,
            envelope.TaskId!,
            EventWebSocketRegistry.SerializeNotification("event.task-log", new { delta = envelope }));

        public void SendSubscription(string id) => Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "subscription.set",
            @params = new
            {
                domain = new { types = new[] { EventCatalog.ReverseDns.IssueCompleted }, match = (string?)null },
                transcript = new { types = new[] { "message.delta" } },
                taskLogs = new[] { new { workflowRunId = "run-1", taskId = "task-1" } },
            },
        }));

        public async Task<JsonElement> NextJsonAsync() =>
            JsonDocument.Parse(await Socket.NextSentAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _run; } catch (OperationCanceledException) { }
            Socket.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
        private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<CloseFrame> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _sendRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _closeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private int _sentCount;
        private int _blockSends;
        private int _blockCloses;
        private Exception? _sendFailure;

        public Task<CloseFrame> Closed => _closed.Task;
        public Task SendStarted => _sendStarted.Task;
        public Task CloseStarted => _closeStarted.Task;
        public int SentCount => Volatile.Read(ref _sentCount);
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public Task<byte[]> NextSentAsync(CancellationToken ct) => _sent.Reader.ReadAsync(ct).AsTask();
        public void ReceiveText(string text) => ReceiveText(Encoding.UTF8.GetBytes(text), true);
        public void ReceiveText(byte[] bytes, bool endOfMessage) =>
            _incoming.Writer.TryWrite(new(bytes, WebSocketMessageType.Text, endOfMessage));
        public void ReceiveBinary(byte[] bytes, bool endOfMessage) =>
            _incoming.Writer.TryWrite(new(bytes, WebSocketMessageType.Binary, endOfMessage));
        public void ReceiveClose(
            WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
            string? reason = null) =>
            _incoming.Writer.TryWrite(new([], WebSocketMessageType.Close, true, status, reason));
        public void PauseSends()
        {
            _sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _sendRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _blockSends, 1);
        }
        public void ReleaseSends()
        {
            Volatile.Write(ref _blockSends, 0);
            _sendRelease.TrySetResult();
        }
        public void PauseCloses()
        {
            _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _blockCloses, 1);
        }
        public void ReleaseCloses()
        {
            Volatile.Write(ref _blockCloses, 0);
            _closeRelease.TrySetResult();
        }
        public void FailNextSend(Exception exception) => Interlocked.Exchange(ref _sendFailure, exception);

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _blockCloses) != 0)
            {
                _closeStarted.TrySetResult();
                await _closeRelease.Task.WaitAsync(cancellationToken);
            }
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _closed.TrySetResult(new(closeStatus, statusDescription));
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            frame.Payload.CopyTo(buffer.Array!, buffer.Offset);
            if (frame.Type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                _closeStatus = frame.CloseStatus;
                _closeStatusDescription = frame.CloseReason;
            }
            return new(frame.Payload.Length, frame.Type, frame.EndOfMessage, frame.CloseStatus, frame.CloseReason);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _blockSends) != 0)
            {
                _sendStarted.TrySetResult();
                await _sendRelease.Task.WaitAsync(cancellationToken);
            }
            var failure = Interlocked.Exchange(ref _sendFailure, null);
            if (failure is not null) throw failure;
            Interlocked.Increment(ref _sentCount);
            _sent.Writer.TryWrite(buffer.ToArray());
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            _sent.Writer.TryComplete();
        }

        private sealed record Frame(
            byte[] Payload,
            WebSocketMessageType Type,
            bool EndOfMessage,
            WebSocketCloseStatus? CloseStatus = null,
            string? CloseReason = null);
    }

    private sealed record CloseFrame(WebSocketCloseStatus Status, string? Reason);

    private sealed class RecordingMatchFailureSink : IEventMatchFailureSink
    {
        public List<(string Source, Exception Exception)> Failures { get; } = [];

        public void Record(string source, Exception exception) => Failures.Add((source, exception));
    }
}
