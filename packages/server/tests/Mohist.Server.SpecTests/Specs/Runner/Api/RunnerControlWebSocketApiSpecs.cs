using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Services.WebSocket;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("IntegrationRunner")]
public sealed class RunnerControlWebSocketApiSpecs(MohistIntegrationFixture fixture)
{
    [Fact]
    public async Task ControlRouteRejectsNonWebSocketRequest()
    {
        using var response = await fixture.Client.GetAsync(
            "/api/runner/ws-route-runner/control",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("websocket_required", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ControlRouteRejectsMissingConnectionIdBeforeUpgrade()
    {
        var client = AuthorizedWebSocketClient();

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(
            new Uri("ws://localhost/api/runner/ws-missing-id/control"),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("uppercase")]
    [InlineData("braces")]
    public async Task ControlRouteRejectsNonCanonicalConnectionId(string format)
    {
        var raw = format == "uppercase"
            ? "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"
            : "{aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa}";
        var client = AuthorizedWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Authorization = $"Bearer {MohistIntegrationFixture.OperatorToken}";
            request.Headers["X-Runner-Connection-Id"] = raw;
        };

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(
            new Uri($"ws://localhost/api/runner/ws-invalid-id-{Guid.NewGuid():N}/control"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ControlRouteRequiresRunnerScopeAuthentication()
    {
        var client = fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers["X-Runner-Connection-Id"] = Guid.NewGuid().ToString();

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(
            new Uri("ws://localhost/api/runner/ws-unauthorized/control"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpgradedConnectionCarriesTypedRequestAndResponse()
    {
        var runnerId = $"ws-runner-{Guid.NewGuid():N}";
        var client = AuthorizedWebSocketClient(Guid.NewGuid());
        using var socket = await client.ConnectAsync(
            new Uri($"ws://localhost/api/runner/{runnerId}/control?buildGitHash=abc123&component=runner&version=1.2.3"),
            TestContext.Current.CancellationToken);
        var registry = fixture.Services.GetRequiredService<RunnerControlWebSocketRegistry>();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        var query = new RunnerWorkspaceQuery(null, "project-1", null, "repo", null, "/work", "branch", "main");

        var pending = registry.SendRequestAsync<WorkspaceCommitDiffParams, RunnerWorkspaceCommitDiffResult>(
            runnerId,
            "workspace.commit-diff",
            new WorkspaceCommitDiffParams(query, "deadbeef"),
            TestContext.Current.CancellationToken);
        var requestBytes = new byte[4096];
        var received = await socket.ReceiveAsync(requestBytes, TestContext.Current.CancellationToken);
        var request = JsonDocument.Parse(requestBytes.AsMemory(0, received.Count));
        Assert.Equal("workspace.commit-diff", request.RootElement.GetProperty("method").GetString());
        var id = request.RootElement.GetProperty("id").GetString();
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            result = new { diff = "expected diff" },
        }, JSON.Options);
        await socket.SendAsync(response, WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        Assert.Equal("expected diff", (await pending).Diff);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandshakePropagatesCompleteRuntimeIdentityWithSourceRevisionFallback()
    {
        var runnerId = $"ws-identity-{Guid.NewGuid():N}";
        await fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"host-{Guid.NewGuid():N}",
        });
        var client = AuthorizedWebSocketClient(Guid.NewGuid());
        using var socket = await client.ConnectAsync(
            new Uri($"ws://localhost/api/runner/{runnerId}/control" +
                "?buildGitHash=build-hash&component=runner&version=1.2.3" +
                "&treeHash=tree-hash&artifactDigest=artifact-digest&releaseId=release-id&generation=17"),
            TestContext.Current.CancellationToken);
        var registry = fixture.Services.GetRequiredService<RunnerControlWebSocketRegistry>();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);

        var info = await fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal("build-hash", info.BuildGitHash);
        Assert.Equal("runner", info.Component);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal("build-hash", info.SourceRevision);
        Assert.Equal("tree-hash", info.TreeHash);
        Assert.Equal("artifact-digest", info.ArtifactDigest);
        Assert.Equal("release-id", info.ReleaseId);
        Assert.Equal(17, info.Generation);
        Assert.False(string.IsNullOrWhiteSpace(info.ConnectionGeneration));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunnerBoundCredentialAcceptsOwnRouteAndRejectsDifferentRoute()
    {
        var runnerId = $"ws-bound-{Guid.NewGuid():N}";
        var token = await EnrollRunnerAsync(runnerId);
        var ownClient = RunnerWebSocketClient(token, Guid.NewGuid());
        using var own = await ownClient.ConnectAsync(
            new Uri($"ws://localhost/api/runner/{runnerId}/control"),
            TestContext.Current.CancellationToken);
        var otherClient = RunnerWebSocketClient(token, Guid.NewGuid());

        await Assert.ThrowsAnyAsync<Exception>(() => otherClient.ConnectAsync(
            new Uri($"ws://localhost/api/runner/{runnerId}-other/control"),
            TestContext.Current.CancellationToken));
        await own.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DuplicateActiveConnectionIdIsRejectedBeforeUpgrade()
    {
        var connectionId = Guid.NewGuid();
        var firstClient = AuthorizedWebSocketClient(connectionId);
        using var first = await firstClient.ConnectAsync(
            new Uri($"ws://localhost/api/runner/ws-duplicate-one-{Guid.NewGuid():N}/control"),
            TestContext.Current.CancellationToken);
        var secondClient = AuthorizedWebSocketClient(connectionId);

        await Assert.ThrowsAnyAsync<Exception>(() => secondClient.ConnectAsync(
            new Uri($"ws://localhost/api/runner/ws-duplicate-two-{Guid.NewGuid():N}/control"),
            TestContext.Current.CancellationToken));
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReplacementFencesOldConnectionAndStaleCloseKeepsNewLease()
    {
        var runnerId = $"ws-replace-{Guid.NewGuid():N}";
        var firstId = Guid.NewGuid();
        var firstClient = AuthorizedWebSocketClient(firstId);
        using var first = await firstClient.ConnectAsync(
            new Uri($"ws://localhost/api/runner/{runnerId}/control"),
            TestContext.Current.CancellationToken);
        var registry = fixture.Services.GetRequiredService<RunnerControlWebSocketRegistry>();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        var query = new RunnerWorkspaceQuery(null, "project-1", null, "repo", null, "/work", "branch", "main");
        var interrupted = registry.SendRequestAsync<WorkspaceCommitDiffParams, RunnerWorkspaceCommitDiffResult>(
            runnerId,
            "workspace.commit-diff",
            new WorkspaceCommitDiffParams(query, "first"),
            TestContext.Current.CancellationToken);
        var firstBuffer = new byte[4096];
        await first.ReceiveAsync(firstBuffer, TestContext.Current.CancellationToken);

        var secondId = Guid.NewGuid();
        var secondClient = AuthorizedWebSocketClient(secondId);
        using var second = await secondClient.ConnectAsync(
            new Uri($"ws://localhost/api/runner/{runnerId}/control"),
            TestContext.Current.CancellationToken);
        var replaced = await first.ReceiveAsync(firstBuffer, TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, replaced.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, first.CloseStatus);
        Assert.Equal("Replaced", first.CloseStatusDescription);
        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() => interrupted);
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);

        var tracker = fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        Assert.Equal(secondId.ToString(), tracker.GetConnectionId(runnerId));
        var current = registry.SendRequestAsync<WorkspaceCommitDiffParams, RunnerWorkspaceCommitDiffResult>(
            runnerId,
            "workspace.commit-diff",
            new WorkspaceCommitDiffParams(query, "second"),
            TestContext.Current.CancellationToken);
        var secondBuffer = new byte[4096];
        var received = await second.ReceiveAsync(secondBuffer, TestContext.Current.CancellationToken);
        var request = JsonDocument.Parse(secondBuffer.AsMemory(0, received.Count));
        var id = request.RootElement.GetProperty("id").GetString();
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            result = new { diff = "replacement response" },
        }, JSON.Options);
        await second.SendAsync(response, WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        Assert.Equal("replacement response", (await current).Diff);
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledInstallationWaiterDoesNotSupersedeInstaller()
    {
        var runnerId = $"ws-install-race-{Guid.NewGuid():N}";
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var tracker = new RunnerConnectionTracker();
        var registry = new RunnerControlWebSocketRegistry(
            tracker,
            fixture.Grains,
            fixture.Services.GetRequiredService<TimeProvider>(),
            fixture.Services.GetRequiredService<ILoggerFactory>());
        var firstAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.InstallationWaiting = (_, connectionId) =>
        {
            if (connectionId == secondId) secondWaiting.TrySetResult();
        };
        registry.InstallationAcquiredAsync = async (_, connectionId, ct) =>
        {
            if (connectionId != firstId) return;
            firstAcquired.TrySetResult();
            await releaseFirst.Task.WaitAsync(ct);
        };
        Assert.True(registry.TryReserve(firstId, out var firstReservation));
        Assert.True(registry.TryReserve(secondId, out var secondReservation));
        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource();
        using var firstSocket = new BlockingWebSocket();
        using var secondSocket = new BlockingWebSocket();
        var handshake = new RunnerControlHandshake(null, null, null, null, null, null, null, null);

        var firstRun = registry.RunAsync(runnerId, firstReservation, firstSocket, handshake, firstStop.Token);
        await firstAcquired.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondRun = registry.RunAsync(runnerId, secondReservation, secondSocket, handshake, secondStop.Token);
        await secondWaiting.Task.WaitAsync(TestContext.Current.CancellationToken);

        secondStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondRun);
        releaseFirst.TrySetResult();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);

        Assert.Equal(firstId.ToString("D"), tracker.GetConnectionId(runnerId));
        Assert.Equal(WebSocketState.Open, firstSocket.State);
        var disconnected = registry.WaitForCurrentDisconnectionAsync(runnerId, TestContext.Current.CancellationToken);
        Assert.False(disconnected.IsCompleted);

        firstStop.Cancel();
        await firstRun;
        await disconnected;
    }

    [Fact]
    public async Task ActiveDisconnectNotifiesTrackedAgentSession()
    {
        var runnerId = $"ws-session-runner-{Guid.NewGuid():N}";
        var sessionId = $"ws-session-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var session = fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, "project-websocket-disconnect")
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-connection")
                .WithLabel(GenericAgentSessionMetadata.AgentId, "agent-websocket-disconnect")));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        var persistence = session.PersistenceCheckpoint(fixture.Persistence);
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, "{\"activity\":\"active\"}") },
            runtimeSessionId));
        await persistence.WaitAsync();
        var tracker = fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        tracker.RegisterSession(runnerId, sessionId);
        var client = AuthorizedWebSocketClient(Guid.NewGuid());
        using var socket = await client.ConnectAsync(
            new Uri($"ws://localhost/api/runner/{runnerId}/control"),
            TestContext.Current.CancellationToken);
        var registry = fixture.Services.GetRequiredService<RunnerControlWebSocketRegistry>();
        await registry.WaitForConnectionAsync(runnerId, TestContext.Current.CancellationToken);
        var disconnected = registry.WaitForCurrentDisconnectionAsync(runnerId, TestContext.Current.CancellationToken);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
        await disconnected;

        Assert.Equal("unknown", (await session.GetAsync())!.Status);
    }

    private Microsoft.AspNetCore.TestHost.WebSocketClient AuthorizedWebSocketClient(Guid? connectionId = null)
    {
        var client = fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Authorization = $"Bearer {MohistIntegrationFixture.OperatorToken}";
            if (connectionId is not null)
                request.Headers["X-Runner-Connection-Id"] = connectionId.Value.ToString();
        };
        return client;
    }

    private Microsoft.AspNetCore.TestHost.WebSocketClient RunnerWebSocketClient(string token, Guid connectionId)
    {
        var client = fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Authorization = $"Bearer {token}";
            request.Headers["X-Runner-Connection-Id"] = connectionId.ToString("D");
        };
        return client;
    }

    private async Task<string> EnrollRunnerAsync(string runnerId)
    {
        using var enrollment = await fixture.Client.PostAsJsonAsync("/api/runners/enrollment-tokens", new { });
        enrollment.EnsureSuccessStatusCode();
        var enrollmentToken = JsonDocument.Parse(await enrollment.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString();
        using var registration = await fixture.Client.PostAsJsonAsync("/api/runners/register", new
        {
            token = enrollmentToken,
            runnerId,
        });
        registration.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await registration.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private sealed class BlockingWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly TaskCompletionSource _receive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            await _receive.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _receive.TrySetResult();
        }
    }
}
