using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("SlackApiSurface")]
public sealed class SlackReplyAnchorIngressSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly List<string> _runnerIds = [];
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackReplyAnchorIngressSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        foreach (var runnerId in _runnerIds)
        {
            tracker.Unregister(runnerId);
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task Direct_message_initial_dispatch_preserves_exact_reply_anchor_across_replay()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = await RegisterRunnerAsync(connection.ProjectId);
        const string conversationId = "D-REPLY-ANCHOR";
        const string messageTs = "1710000000.000100";

        var accepted = await PostDirectMessageAsync(connection, conversationId, messageTs, "start the work");
        var sessionId = accepted.GetProperty("sessionId").GetString()!;
        var initial = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetInitialLaunchAsync()
            ?? throw new InvalidOperationException("DM ingress did not create the initial Session input.");
        var inputId = initial.Input!.Id;
        var jobKey = initial.Turn!.JobId!;
        var dispatch = await PollInitialDispatchAsync(runnerId, jobKey);

        Assert.Equal("slack", InitialExecutionSource(dispatch));
        AssertReplyAnchor(
            InitialReplyAnchor(dispatch),
            connection,
            conversationId,
            threadRootMessageId: messageTs,
            triggeringMessageId: messageTs,
            sessionId,
            dispatchRef: $"slack:{sessionId}:{inputId}");

        var replay = await PostDirectMessageAsync(connection, conversationId, messageTs, "start the work");

        Assert.Equal(sessionId, replay.GetProperty("sessionId").GetString());
        Assert.Equal(inputId, (await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .GetInitialLaunchAsync())!.Input!.Id);
        Assert.Equal(1, _fixture.AgentJobDispatches.PreparedCount(jobKey));

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var anchor = ValidationRequest(connection, conversationId, messageTs, messageTs, sessionId, $"slack:{sessionId}:{inputId}");
        Assert.Equal(new SlackReplyAnchorValidationResult(true, true), await grain.ValidateSlackReplyAnchorAsync(anchor));
        var otherConnection = await CreateConnectionAsync();
        Assert.False((await grain.ValidateSlackReplyAnchorAsync(anchor with { ConnectionId = otherConnection.Id })).Valid);
        Assert.False((await grain.ValidateSlackReplyAnchorAsync(anchor with { TriggeringMessageId = "invented-trigger" })).Valid);
        Assert.False((await grain.ValidateSlackReplyAnchorAsync(anchor with { DispatchRef = "invented-dispatch" })).Valid);

        using var validReply = await PostLegacyAnchoredReplyAsync(connection, anchor, "initial answer");
        Assert.True(validReply.IsSuccessStatusCode, await validReply.Content.ReadAsStringAsync());
        using var currentRetry = await PostAnchoredReplyAsync(connection, anchor, "initial answer");
        Assert.True(currentRetry.IsSuccessStatusCode, await currentRetry.Content.ReadAsStringAsync());
        using var forgedReply = await PostAnchoredReplyAsync(connection, anchor with { ConnectionId = otherConnection.Id }, "forged");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, forgedReply.StatusCode);
        using var inventedSessionReply = await PostAnchoredReplyAsync(
            connection, anchor with { SessionId = "invented-session" }, "forged");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, inventedSessionReply.StatusCode);

        await grain.MarkTurnTerminalAsync(initial.Turn!.Id, AgentTurnStatus.Completed, null);
        Assert.Equal(new SlackReplyAnchorValidationResult(true, false), await grain.ValidateSlackReplyAnchorAsync(anchor));
        using var retry = await PostAnchoredReplyAsync(connection, anchor, "initial answer");
        Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        using var staleReply = await PostAnchoredReplyAsync(connection, anchor, "different stale answer");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, staleReply.StatusCode);
    }

    [Fact]
    public async Task Thread_root_mention_initial_dispatch_preserves_exact_reply_anchor_across_replay()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = await RegisterRunnerAsync(connection.ProjectId);
        const string conversationId = "C-REPLY-ANCHOR";
        const string messageTs = "1710000000.000200";

        var accepted = await PostChannelAsync(
            connection,
            conversationId,
            messageTs,
            threadTs: null,
            mentionedUserIds: [connection.BotUserId],
            text: $"<@{connection.BotUserId}> inspect the problem");
        var sessionId = accepted.GetProperty("sessionId").GetString()!;
        var initial = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetInitialLaunchAsync()
            ?? throw new InvalidOperationException("Thread root ingress did not create the initial Session input.");
        var inputId = initial.Input!.Id;
        var jobKey = initial.Turn!.JobId!;
        var dispatch = await PollInitialDispatchAsync(runnerId, jobKey);

        Assert.Equal("slack", InitialExecutionSource(dispatch));
        AssertReplyAnchor(
            InitialReplyAnchor(dispatch),
            connection,
            conversationId,
            threadRootMessageId: messageTs,
            triggeringMessageId: messageTs,
            sessionId,
            dispatchRef: $"slack:{sessionId}:{inputId}");

        var replay = await PostChannelAsync(
            connection,
            conversationId,
            messageTs,
            threadTs: null,
            mentionedUserIds: [connection.BotUserId],
            text: $"<@{connection.BotUserId}> inspect the problem");

        Assert.Equal(sessionId, replay.GetProperty("sessionId").GetString());
        Assert.Equal(inputId, (await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .GetInitialLaunchAsync())!.Input!.Id);
        Assert.Equal(1, _fixture.AgentJobDispatches.PreparedCount(jobKey));

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var anchor = ValidationRequest(
            connection, conversationId, messageTs, messageTs, sessionId, $"slack:{sessionId}:{inputId}");
        await grain.MarkTurnTerminalAsync(initial.Turn!.Id, AgentTurnStatus.Completed, null);
        using var firstSendAfterTerminal = await PostAnchoredReplyAsync(connection, anchor, "too late for a first send");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, firstSendAfterTerminal.StatusCode);
    }

    [Fact]
    public async Task Thread_followup_dispatch_preserves_exact_reply_anchor_without_redelivery_on_replay()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = await RegisterRunnerAsync(connection.ProjectId);
        const string runnerConnectionId = "runner-reply-anchor";
        const string conversationId = "C-REPLY-FOLLOWUP";
        const string rootTs = "1710000000.000300";
        const string followupTs = "1710000000.000310";

        var root = await PostChannelAsync(
            connection,
            conversationId,
            rootTs,
            threadTs: null,
            mentionedUserIds: [connection.BotUserId],
            text: $"<@{connection.BotUserId}> establish a session");
        var sessionId = root.GetProperty("sessionId").GetString()!;
        var rootInitial = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetInitialLaunchAsync()
            ?? throw new InvalidOperationException("Thread root ingress did not create the initial Session input.");
        var rootDispatch = await PollInitialDispatchAsync(runnerId, rootInitial.Turn!.JobId!);
        Assert.Equal("slack", InitialExecutionSource(rootDispatch));
        await BindRuntimeSessionAsync(connection, runnerId, sessionId, rootDispatch);
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).MarkTurnTerminalAsync(
            root.GetProperty("turnId").GetString()!, AgentTurnStatus.Completed, null);

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(runnerId, runnerConnectionId);
        try
        {
            var accepted = await PostChannelAsync(
                connection,
                conversationId,
                followupTs,
                threadTs: rootTs,
                mentionedUserIds: [],
                text: "continue with the next step");

            Assert.True(accepted.GetProperty("followup").GetBoolean());
            Assert.Equal(sessionId, accepted.GetProperty("sessionId").GetString());
            var delivery = Assert.Single(
                runnerHub.SentMessages,
                message => message.ConnectionId == runnerId && message.Method == "session.followup");
            var payload = JsonSerializer.SerializeToElement(delivery.Arguments.Single(), JSON.Options);
            var operationId = payload.GetProperty("operationId").GetString()!;

            Assert.Equal("slack", payload.GetProperty("executionSource").GetString());
            AssertSkillParity(InitialReplyAnchor(rootDispatch), payload.GetProperty("slackExecutionContext"));
            AssertReplyAnchor(
                payload.GetProperty("slackExecutionContext"),
                connection,
                conversationId,
                threadRootMessageId: rootTs,
                triggeringMessageId: followupTs,
                sessionId,
                dispatchRef: operationId);

            var replay = await PostChannelAsync(
                connection,
                conversationId,
                followupTs,
                threadTs: rootTs,
                mentionedUserIds: [],
                text: "continue with the next step");

            Assert.Equal(sessionId, replay.GetProperty("sessionId").GetString());
            Assert.Equal(accepted.GetProperty("inputId").GetString(), replay.GetProperty("inputId").GetString());
            Assert.Equal(accepted.GetProperty("turnId").GetString(), replay.GetProperty("turnId").GetString());
            Assert.Single(
                runnerHub.SentMessages,
                message => message.ConnectionId == runnerId && message.Method == "session.followup");

            var anchor = ValidationRequest(
                connection, conversationId, rootTs, followupTs, sessionId, operationId);
            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            Assert.Equal(new SlackReplyAnchorValidationResult(true, true), await grain.ValidateSlackReplyAnchorAsync(anchor));
            Assert.False((await grain.ValidateSlackReplyAnchorAsync(anchor with { DispatchRef = "invented-followup-dispatch" })).Valid);
            using var validReply = await PostAnchoredReplyAsync(connection, anchor, "follow-up answer");
            Assert.True(validReply.IsSuccessStatusCode, await validReply.Content.ReadAsStringAsync());
            await grain.MarkFollowupTurnTerminalAsync(operationId, AgentTurnStatus.Completed, null);
            Assert.Equal(new SlackReplyAnchorValidationResult(true, false), await grain.ValidateSlackReplyAnchorAsync(anchor));
            using var retry = await PostAnchoredReplyAsync(connection, anchor, "follow-up answer");
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
            using var inventedRetry = await PostAnchoredReplyAsync(
                connection, anchor with { DispatchRef = "invented-terminal-dispatch" }, "follow-up answer");
            Assert.Equal(System.Net.HttpStatusCode.Conflict, inventedRetry.StatusCode);
        }
        finally
        {
            tracker.Unregister(runnerId);
        }
    }

    [Fact]
    public async Task Direct_message_followup_anchors_reply_to_its_own_triggering_message()
    {
        var connection = await CreateConnectionAsync();
        var runnerId = await RegisterRunnerAsync(connection.ProjectId);
        const string conversationId = "D-REPLY-FOLLOWUP";
        const string initialTs = "1710000000.000400";
        const string followupTs = "1710000000.000410";

        var acceptedInitial = await PostDirectMessageAsync(connection, conversationId, initialTs, "start the work");
        var sessionId = acceptedInitial.GetProperty("sessionId").GetString()!;
        var initial = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetInitialLaunchAsync()
            ?? throw new InvalidOperationException("DM ingress did not create the initial Session input.");
        var initialDispatch = await PollInitialDispatchAsync(runnerId, initial.Turn!.JobId!);
        await BindRuntimeSessionAsync(connection, runnerId, sessionId, initialDispatch);
        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).MarkTurnTerminalAsync(
            acceptedInitial.GetProperty("turnId").GetString()!, AgentTurnStatus.Completed, null);

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(runnerId, "runner-reply-anchor");
        try
        {
            var accepted = await PostDirectMessageAsync(connection, conversationId, followupTs, "continue with the next step");

            Assert.True(accepted.GetProperty("followup").GetBoolean());
            Assert.Equal(sessionId, accepted.GetProperty("sessionId").GetString());
            var delivery = Assert.Single(
                runnerHub.SentMessages,
                message => message.ConnectionId == runnerId && message.Method == "session.followup");
            var payload = JsonSerializer.SerializeToElement(delivery.Arguments.Single(), JSON.Options);
            var operationId = payload.GetProperty("operationId").GetString()!;
            Assert.Equal("slack", payload.GetProperty("executionSource").GetString());

            // The DM follow-up anchors to its own triggering message so the
            // terminal reply replaces the progress projection in place.
            AssertReplyAnchor(
                payload.GetProperty("slackExecutionContext"),
                connection,
                conversationId,
                threadRootMessageId: followupTs,
                triggeringMessageId: followupTs,
                sessionId,
                dispatchRef: operationId);

            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var anchor = ValidationRequest(
                connection, conversationId, followupTs, followupTs, sessionId, operationId);
            Assert.Equal(new SlackReplyAnchorValidationResult(true, true), await grain.ValidateSlackReplyAnchorAsync(anchor));

            // The session-initial root must no longer validate a follow-up
            // reply: anchoring to a stale prior thread is the defect.
            Assert.False((await grain.ValidateSlackReplyAnchorAsync(
                anchor with { ThreadRootMessageId = initialTs })).Valid);

            using var validReply = await PostAnchoredReplyAsync(connection, anchor, "follow-up answer");
            Assert.True(validReply.IsSuccessStatusCode, await validReply.Content.ReadAsStringAsync());
            using var staleRootReply = await PostAnchoredReplyAsync(
                connection, anchor with { ThreadRootMessageId = initialTs }, "stale-thread answer");
            Assert.Equal(System.Net.HttpStatusCode.Conflict, staleRootReply.StatusCode);
        }
        finally
        {
            tracker.Unregister(runnerId);
        }
    }

    [Fact]
    public async Task Legacy_initial_root_fallback_validates_an_active_followup_anchor()
    {
        var connection = await CreateConnectionAsync();
        var sessionId = $"legacy-reply-anchor-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        const string conversationId = "C-LEGACY-ROOT";
        const string rootTs = "1710000000.000600";
        const string followupTs = "1710000000.000601";
        var metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = connection.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [GenericAgentSessionMetadata.AgentId] = "legacy-agent",
            [AgentSessionQueryMetadataKeys.ConnectionId] = connection.Id,
            [AgentSessionQueryMetadataKeys.SlackWorkspaceTeamId] = connection.WorkspaceTeamId,
            [AgentSessionQueryMetadataKeys.SlackConversationId] = conversationId,
            [AgentSessionQueryMetadataKeys.SlackThreadTs] = rootTs,
        });
        var legacyProvenance = new AgentSessionInputProvenance(
            "slack", connection.WorkspaceTeamId, conversationId, rootTs,
            "U_OWNER", rootTs, connection.Id, BoundThreadRootMessageId: null);
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "legacy-input", "legacy-turn", "start", "agent-connection", "legacy-job",
            metadata, Runtime: "opencode", Provenance: legacyProvenance));
        await grain.OpenAsync(new OpenAgentSessionCommand("legacy-runner", "opencode", Metadata: metadata));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "legacy-runtime-session", Runtime: "opencode", ExpectedRuntime: "opencode", ExpectedRunnerId: "legacy-runner"));
        await grain.MarkInitialTurnTerminalAsync("legacy-job", AgentTurnStatus.Completed, null);
        var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "continue", "agent-session-followup", "legacy-followup",
            Provenance: legacyProvenance with { MessageId = followupTs }));

        Assert.Equal(new SlackReplyAnchorValidationResult(true, true), await grain.ValidateSlackReplyAnchorAsync(ValidationRequest(
            connection, conversationId, rootTs, followupTs, sessionId, followup.OperationId)));
    }

    private async Task<string> RegisterRunnerAsync(string projectId)
    {
        var runnerId = $"slack-reply-anchor-{Guid.NewGuid():N}";
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await _fixture.Grains.GetGrain<IRunnerGrain>(staleId).UnregisterAsync();
        Assert.Empty(await registry.ListRunnerIdsAsync());

        using var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
            runtimeCatalogs = CapabilityCatalogTestHelpers.Create(),
        });
        register.EnsureSuccessStatusCode();
        using var slots = await _fixture.Client.PatchAsJsonAsync($"/api/runner/{runnerId}", new { slots = 1 });
        slots.EnsureSuccessStatusCode();
        _runnerIds.Add(runnerId);
        await TestWait.ForAsync(
            () => _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync(),
            state => state.Status == RunnerStatus.Online,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"Runner '{runnerId}' to reach Online");
        return runnerId;
    }

    private async Task<JsonElement> PollInitialDispatchAsync(string runnerId, string jobKey)
    {
        var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
        await AgentJobConvergence.WaitForAssignmentPreparedAsync(job);
        using var poll = await _fixture.Client.PostRunnerPollAsync(runnerId);
        var dispatch = Assert.Single(await poll.ReadDispatchElementsAsync());
        var assignment = await AgentJobConvergence.WaitForRunnerAcceptedAsync(job);
        Assert.Equal(runnerId, assignment.RunnerId);
        Assert.Equal(dispatch.GetProperty("workId").GetString(), assignment.CurrentWorkId);
        return dispatch;
    }

    private async Task BindRuntimeSessionAsync(
        AgentConnection connection,
        string runnerId,
        string sessionId,
        JsonElement dispatch)
    {
        using var opened = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/agent-sessions/{connection.ProjectId}/{sessionId}/open",
            new
            {
                workId = dispatch.GetProperty("workId").GetString(),
                workType = "agent-job",
                stage = "agent",
                title = "Slack reply anchor session",
            });
        opened.EnsureSuccessStatusCode();
        using var attached = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/agent-sessions/{connection.ProjectId}/{sessionId}/attach",
            new
            {
                runtimeSessionId = $"runtime-{sessionId}",
                workDir = "/mohist-tests/slack-reply-anchor",
                processPid = 1234,
            });
        attached.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> PostDirectMessageAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            senderSlackUserId = "U_OWNER",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentionedUserIds,
        string text)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds,
            senderSlackUserId = "U_OWNER",
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static JsonElement InitialReplyAnchor(JsonElement dispatch)
    {
        using var with = JsonDocument.Parse(dispatch.GetProperty("with").GetString()!);
        return with.RootElement.GetProperty("slackExecutionContext").Clone();
    }

    private static string? InitialExecutionSource(JsonElement dispatch)
    {
        using var with = JsonDocument.Parse(dispatch.GetProperty("with").GetString()!);
        return with.RootElement.GetProperty("executionSource").GetString();
    }

    private static void AssertSkillParity(JsonElement initial, JsonElement followup)
    {
        var initialSkill = initial.GetProperty("collaborationSkill");
        var followupSkill = followup.GetProperty("collaborationSkill");
        Assert.Equal(initialSkill.GetProperty("name").GetString(), followupSkill.GetProperty("name").GetString());
        Assert.Equal(initialSkill.GetProperty("version").GetString(), followupSkill.GetProperty("version").GetString());
        Assert.Equal(initialSkill.GetProperty("instructions").GetString(), followupSkill.GetProperty("instructions").GetString());
        Assert.Equal(initialSkill.GetProperty("contentHash").GetString(), followupSkill.GetProperty("contentHash").GetString());
    }

    private static void AssertReplyAnchor(
        JsonElement context,
        AgentConnection connection,
        string conversationId,
        string threadRootMessageId,
        string triggeringMessageId,
        string sessionId,
        string dispatchRef)
    {
        Assert.Equal(1, context.GetProperty("version").GetInt32());
        var anchor = context.GetProperty("replyAnchor");
        Assert.Equal(connection.WorkspaceTeamId, anchor.GetProperty("workspaceId").GetString());
        Assert.Equal(conversationId, anchor.GetProperty("conversationId").GetString());
        Assert.Equal(threadRootMessageId, anchor.GetProperty("threadRootMessageId").GetString());
        Assert.Equal(triggeringMessageId, anchor.GetProperty("triggeringMessageId").GetString());
        Assert.Equal("U_OWNER", anchor.GetProperty("initiatingMemberId").GetString());
        Assert.Equal(connection.Id, anchor.GetProperty("connectionId").GetString());
        Assert.Equal(sessionId, anchor.GetProperty("sessionId").GetString());
        Assert.Equal(dispatchRef, anchor.GetProperty("dispatchRef").GetString());
        Assert.False(anchor.TryGetProperty("appToken", out _));
        Assert.False(anchor.TryGetProperty("botToken", out _));
        Assert.DoesNotContain("xapp-anchor-secret", context.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-anchor-secret", context.GetRawText(), StringComparison.Ordinal);
    }

    private static SlackReplyAnchorValidationRequest ValidationRequest(
        AgentConnection connection,
        string conversationId,
        string threadRootMessageId,
        string triggeringMessageId,
        string sessionId,
        string dispatchRef) =>
        new(
            connection.ProjectId,
            connection.WorkspaceTeamId,
            conversationId,
            threadRootMessageId,
            triggeringMessageId,
            connection.Id,
            sessionId,
            dispatchRef);

    private async Task<HttpResponseMessage> PostAnchoredReplyAsync(
        AgentConnection connection,
        SlackReplyAnchorValidationRequest anchor,
        string text) =>
        await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new
            {
                workspaceTeamId = anchor.WorkspaceId,
                conversationId = anchor.ConversationId,
                threadTs = anchor.ThreadRootMessageId,
                connectionId = anchor.ConnectionId,
                sessionId = anchor.SessionId,
                triggeringMessageId = anchor.TriggeringMessageId,
                dispatchRef = anchor.DispatchRef,
                text,
            });

    private async Task<HttpResponseMessage> PostLegacyAnchoredReplyAsync(
        AgentConnection connection,
        SlackReplyAnchorValidationRequest anchor,
        string text) =>
        await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{connection.ProjectId}/slack-connections/reply",
            new
            {
                conversationId = anchor.ConversationId,
                threadTs = anchor.ThreadRootMessageId,
                connectionId = anchor.ConnectionId,
                triggeringMessageId = anchor.TriggeringMessageId,
                dispatchRef = anchor.DispatchRef,
                text,
            });

    private async Task<AgentConnection> CreateConnectionAsync()
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "Mohist Agent",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = "Mohist Agent",
                Status = AgentStatus.Active,
                Instructions = "Handle Slack requests.",
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T123",
            AppId = "A123",
            BotUserId = "U123",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_OWNER",
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, "T123");
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = "T123",
            AgentConnectionId = id,
            AppId = $"A_SPEC_{Guid.NewGuid():N}",
            BotUserId = "U123",
            AppLifecycle = SlackAppLifecycle.Created,
            Authorization = SlackAuthorizationState.Authorized,
            RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
            DesiredManifestVersion = 1,
            DesiredManifestHash = "desired",
            VerifiedScopesJson = "[]",
            OperationFence = 0,
            AppLevelTokenRef = agentAppId,
            BotTokenRef = agentAppId,
            BindingState = SlackAgentAppBindingState.Bound,
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-anchor-secret"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-anchor-secret"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-anchor-secret"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-anchor-secret"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
            BotUserId = "U123",
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
