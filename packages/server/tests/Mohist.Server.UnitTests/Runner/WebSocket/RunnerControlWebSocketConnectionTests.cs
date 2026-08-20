using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Services.WebSocket;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.WebSocket;

public sealed class RunnerControlWebSocketConnectionTests
{
    [Fact]
    public async Task RequestCorrelatesTypedFragmentedResponse()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test", new TestParams("input"), TestContext.Current.CancellationToken);
        var request = await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken);
        var id = JsonDocument.Parse(request).RootElement.GetProperty("id").GetString();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            result = new { value = "output" },
        }, JSON.Options);

        fixture.Socket.ReceiveText(json[..10], endOfMessage: false);
        fixture.Socket.ReceiveText(json[10..], endOfMessage: true);

        Assert.Equal(new TestResult("output"), await response);
    }

    [Fact]
    public async Task RemoteErrorPreservesTypedError()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test", new TestParams("input"), TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        fixture.Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code = -32602, message = "invalid", data = new { field = "value" } },
        }, JSON.Options));

        var error = await Assert.ThrowsAsync<RunnerControlRemoteException>(() => response);
        Assert.Equal(-32602, error.Code);
        Assert.Equal("invalid", error.Message);
        Assert.Equal("value", error.ErrorData?.GetProperty("field").GetString());
    }

    [Fact]
    public async Task RequestTimesOutAtFifteenSeconds()
    {
        var time = new FakeTimeProvider();
        await using var fixture = new ConnectionFixture(time);
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test", new TestParams("input"), TestContext.Current.CancellationToken);
        await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(15));

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => response);
        Assert.Equal(WebSocketState.Open, fixture.Socket.State);
    }

    [Fact]
    public async Task RequestTimeoutStartsBeforeBlockedEnqueueCallbackReturns()
    {
        var time = new FakeTimeProvider();
        await using var fixture = new ConnectionFixture(time);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCalls = 0;
        var response = Task.Run(async () => await fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test",
            new TestParams("input"),
            allowsNull: false,
            () =>
            {
                Interlocked.Increment(ref callbackCalls);
                callbackEntered.TrySetResult();
                callbackRelease.Task.GetAwaiter().GetResult();
            },
            TestContext.Current.CancellationToken));
        await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(15));
        callbackRelease.TrySetResult();

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => response);
        Assert.Equal(1, callbackCalls);
    }

    [Fact]
    public async Task ThrowingEnqueueCallbackDoesNotInvalidateTypedResponse()
    {
        await using var fixture = new ConnectionFixture();
        var callbackCalls = 0;
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test",
            new TestParams("input"),
            allowsNull: false,
            () =>
            {
                Interlocked.Increment(ref callbackCalls);
                throw new InvalidOperationException("observer failed");
            },
            TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        fixture.Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new { value = "output" },
        }, JSON.Options));

        Assert.Equal(new TestResult("output"), await response);
        Assert.Equal(1, callbackCalls);
    }

    [Fact]
    public async Task DisconnectCompletesPendingRequestUnavailable()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test", new TestParams("input"), TestContext.Current.CancellationToken);
        await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken);

        fixture.Socket.ReceiveClose();

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => response);
    }

    [Fact]
    public async Task ThirtyThirdRequestFailsWithoutClosingConnection()
    {
        await using var fixture = new ConnectionFixture(start: false);
        var pending = Enumerable.Range(0, 32)
            .Select(index => fixture.Connection.SendRequestAsync<TestParams, TestResult>(
                "test", new TestParams(index.ToString()), TestContext.Current.CancellationToken))
            .ToArray();

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() =>
            fixture.Connection.SendRequestAsync<TestParams, TestResult>(
                "test", new TestParams("33"), TestContext.Current.CancellationToken));
        Assert.Equal(WebSocketState.Open, fixture.Socket.State);

        await fixture.Connection.FenceAsync(WebSocketCloseStatus.NormalClosure, "test complete");
        await Task.WhenAll(pending.Select(IgnoreUnavailable));
    }

    [Fact]
    public async Task SharedQueueSaturationClosesWith1013()
    {
        await using var fixture = new ConnectionFixture(blockSends: true);
        await fixture.Connection.SendNotificationAsync("test", new TestParams("blocked"), TestContext.Current.CancellationToken);
        await fixture.Socket.SendStarted.WaitAsync(TestContext.Current.CancellationToken);
        for (var index = 0; index < 64; index++)
            await fixture.Connection.SendNotificationAsync("test", new TestParams(index.ToString()), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() =>
            fixture.Connection.SendNotificationAsync("test", new TestParams("overflow"), TestContext.Current.CancellationToken));

        var close = await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal((WebSocketCloseStatus)1013, close.Status);
    }

    [Fact]
    public async Task SaturatedQueueRequestThrowsAfterFencing()
    {
        await using var fixture = new ConnectionFixture(start: false);
        for (var index = 0; index < 64; index++)
            await fixture.Connection.SendNotificationAsync("test", new TestParams(index.ToString()), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() =>
            fixture.Connection.SendRequestAsync<TestParams, TestResult>(
                "session.stop", new TestParams("mutating-shaped"), TestContext.Current.CancellationToken));

        Assert.Equal((WebSocketCloseStatus)1013, (await fixture.Socket.Closed).Status);
    }

    [Fact]
    public async Task PreCancelledRequestIsNotEnqueued()
    {
        await using var fixture = new ConnectionFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Connection.SendRequestAsync<TestParams, TestResult>(
                "session.stop", new TestParams("mutating-shaped"), cancelled.Token));

        Assert.Equal(0, fixture.Socket.SentCount);
    }

    [Fact]
    public async Task OversizedFragmentedMessageClosesWith1009()
    {
        await using var fixture = new ConnectionFixture();
        for (var index = 0; index < 256; index++)
            fixture.Socket.ReceiveText(new byte[16 * 1024], endOfMessage: false);
        fixture.Socket.ReceiveText([0], endOfMessage: true);

        var close = await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, close.Status);
    }

    [Fact]
    public async Task ExactFourMiBTextMessageIsAccepted()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test", new TestParams("input"), TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        var prefix = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"value\":\"output\"}},\"padding\":\"");
        var suffix = "\"}"u8.ToArray();
        var message = new byte[4 * 1024 * 1024];
        prefix.CopyTo(message, 0);
        Array.Fill(message, (byte)'x', prefix.Length, message.Length - prefix.Length - suffix.Length);
        suffix.CopyTo(message, message.Length - suffix.Length);
        for (var offset = 0; offset < message.Length; offset += 16 * 1024)
        {
            var count = Math.Min(16 * 1024, message.Length - offset);
            fixture.Socket.ReceiveText(message.AsSpan(offset, count).ToArray(), offset + count == message.Length);
        }

        Assert.Equal(new TestResult("output"), await response);
        Assert.Equal(WebSocketState.Open, fixture.Socket.State);
    }

    [Fact]
    public async Task BinaryAndMixedFragmentsAreProtocolErrors()
    {
        await using var fixture = new ConnectionFixture();
        fixture.Socket.ReceiveBinary([1], true);
        fixture.Socket.ReceiveBinary([2], false);
        fixture.Socket.ReceiveText([3], true);
        fixture.Socket.ReceiveBinary([4], true);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, (await fixture.Socket.Closed).Status);
    }

    [Fact]
    public async Task NullableResultAcceptsJsonNull()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult?>(
            "workspace.diff", new TestParams("input"), allowsNull: true, TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        fixture.Socket.ReceiveText($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":null}}");

        Assert.Null(await response);
    }

    [Fact]
    public async Task NullNonNullableResultCompletesUnavailable()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "session.stop", new TestParams("input"), TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        fixture.Socket.ReceiveText($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":null}}");

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => response);
    }

    [Fact]
    public async Task MissingRequiredResultMemberCompletesUnavailable()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, WorkspaceStatus>(
            "workspace.status", new TestParams("input"), TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        fixture.Socket.ReceiveText($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"reason\":\"missing\"}}}}");

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => response);
    }

    [Fact]
    public async Task MissingRequiredConstructorParameterCompletesUnavailable()
    {
        await using var fixture = new ConnectionFixture();
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "session.stop", new TestParams("input"), TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        fixture.Socket.ReceiveText($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{}}}}");

        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => response);
    }

    [Fact]
    public async Task PeerCloseStatusAndReasonAreAcknowledged()
    {
        await using var fixture = new ConnectionFixture();
        fixture.Socket.ReceiveClose(WebSocketCloseStatus.EndpointUnavailable, "maintenance");

        var close = await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, close.Status);
        Assert.Equal("maintenance", close.Reason);
    }

    [Fact]
    public async Task CloseTimeoutAbortsSocketWithoutConcurrentSend()
    {
        var time = new FakeTimeProvider();
        await using var fixture = new ConnectionFixture(time, blockSends: true, blockClose: true);
        await fixture.Connection.SendNotificationAsync("test", new TestParams("blocked"), TestContext.Current.CancellationToken);
        await fixture.Socket.SendStarted.WaitAsync(TestContext.Current.CancellationToken);

        var fence = fixture.Connection.FenceAsync(WebSocketCloseStatus.NormalClosure, "bounded");
        await fixture.Socket.CloseStarted.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));
        await fence;

        Assert.Equal(WebSocketState.Aborted, fixture.Socket.State);
    }

    [Fact]
    public async Task SendGateTimeoutAbortsSocketWhenSendIgnoresCancellation()
    {
        var time = new FakeTimeProvider();
        await using var fixture = new ConnectionFixture(time, blockSends: true, ignoreSendCancellation: true);
        await fixture.Connection.SendNotificationAsync("test", new TestParams("blocked"), TestContext.Current.CancellationToken);
        await fixture.Socket.SendStarted.WaitAsync(TestContext.Current.CancellationToken);

        var fence = fixture.Connection.FenceAsync(WebSocketCloseStatus.NormalClosure, "bounded");
        time.Advance(TimeSpan.FromSeconds(5));
        await fence;

        Assert.Equal(WebSocketState.Aborted, fixture.Socket.State);

        fixture.Socket.ReleaseSend();
        await fixture.WaitForRunAsync();
        Assert.True(fixture.Connection.IsSocketSendGateAvailable);
    }

    [Fact]
    public async Task ThirdMalformedResponseClosesConnection()
    {
        await using var fixture = new ConnectionFixture();
        fixture.Socket.ReceiveText("[]");
        fixture.Socket.ReceiveText("{}");
        fixture.Socket.ReceiveText("not-json");

        var close = await fixture.Socket.Closed.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.Status);
    }

    [Fact]
    public async Task LateResponseIsIgnoredWithoutClosingConnection()
    {
        var time = new FakeTimeProvider();
        await using var fixture = new ConnectionFixture(time);
        var response = fixture.Connection.SendRequestAsync<TestParams, TestResult>(
            "test", new TestParams("input"), TestContext.Current.CancellationToken);
        var id = RequestId(await fixture.Socket.NextSentAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => response);

        fixture.Socket.ReceiveText(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new { value = "late" },
        }, JSON.Options));
        await fixture.Socket.FrameConsumed.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketState.Open, fixture.Socket.State);
    }

    private static string RequestId(byte[] request) =>
        JsonDocument.Parse(request).RootElement.GetProperty("id").GetString()!;

    private static async Task IgnoreUnavailable(Task task)
    {
        try { await task; }
        catch (RunnerControlUnavailableException) { }
    }

    private sealed record TestParams(string Value);
    private sealed record TestResult(string Value);

    private sealed class ConnectionFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task? _run;

        public ConnectionFixture(
            FakeTimeProvider? time = null,
            bool start = true,
            bool blockSends = false,
            bool blockClose = false,
            bool ignoreSendCancellation = false)
        {
            Socket = new FakeWebSocket(blockSends, blockClose, ignoreSendCancellation);
            Connection = new RunnerControlWebSocketConnection(
                "runner-1", Guid.NewGuid(), Socket, time ?? new FakeTimeProvider(), NullLogger.Instance);
            if (start)
                _run = Connection.RunAsync(_stop.Token);
        }

        public FakeWebSocket Socket { get; }
        public RunnerControlWebSocketConnection Connection { get; }

        public async Task WaitForRunAsync()
        {
            if (_run is not null) await _run;
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.FenceAsync(WebSocketCloseStatus.NormalClosure, "test complete");
            _stop.Cancel();
            Socket.ReleaseSend();
            if (_run is not null)
            {
                try { await _run; }
                catch (OperationCanceledException) { }
            }
            _stop.Dispose();
            Socket.Dispose();
        }
    }

    private sealed class FakeWebSocket(
        bool blockSends,
        bool blockClose,
        bool ignoreSendCancellation) : System.Net.WebSockets.WebSocket
    {
        private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
        private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sendRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CloseFrame> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _frameConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private int _activeSend;
        private int _sentCount;

        public Task SendStarted => _sendStarted.Task;
        public Task<CloseFrame> Closed => _closed.Task;
        public Task FrameConsumed => _frameConsumed.Task;
        public Task CloseStarted => _closeStarted.Task;
        public int SentCount => Volatile.Read(ref _sentCount);
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public Task<byte[]> NextSentAsync(CancellationToken ct) => _sent.Reader.ReadAsync(ct).AsTask();

        public void ReceiveText(string text) => ReceiveText(Encoding.UTF8.GetBytes(text), true);
        public void ReceiveText(byte[] bytes, bool endOfMessage) =>
            _incoming.Writer.TryWrite(new Frame(bytes, WebSocketMessageType.Text, endOfMessage));
        public void ReceiveBinary(byte[] bytes, bool endOfMessage) =>
            _incoming.Writer.TryWrite(new Frame(bytes, WebSocketMessageType.Binary, endOfMessage));
        public void ReceiveClose(
            WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
            string? reason = null) =>
            _incoming.Writer.TryWrite(new Frame([], WebSocketMessageType.Close, true, status, reason));
        public void ReleaseSend() => _sendRelease.TrySetResult();

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _activeSend, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent WebSocket send");
            try
            {
                _closeStarted.TrySetResult();
                if (blockClose) await _closeRelease.Task.WaitAsync(cancellationToken);
                _closeStatus = closeStatus;
                _closeStatusDescription = statusDescription;
                _state = WebSocketState.Closed;
                _closed.TrySetResult(new CloseFrame(closeStatus, statusDescription));
            }
            finally
            {
                Volatile.Write(ref _activeSend, 0);
            }
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
            _frameConsumed.TrySetResult();
            return new WebSocketReceiveResult(
                frame.Payload.Length,
                frame.Type,
                frame.EndOfMessage,
                frame.CloseStatus,
                frame.CloseReason);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _activeSend, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent WebSocket send");
            try
            {
                _sendStarted.TrySetResult();
                if (blockSends)
                {
                    if (ignoreSendCancellation)
                        await _sendRelease.Task;
                    else
                        await _sendRelease.Task.WaitAsync(cancellationToken);
                }
                await _sent.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
                Interlocked.Increment(ref _sentCount);
            }
            finally
            {
                Volatile.Write(ref _activeSend, 0);
            }
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
}
