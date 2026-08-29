using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Spec tests for the DM current-session mapping wired into the Slack DM
/// ingress. Lives in its own file so the parent
/// <c>SlackConnectionApiSpecs</c> stays under the C# test-file size ratchet
/// (design/testing.md). Companion to the unit-level
/// <c>SlackDmSessionMappingStoreTests</c> (provider-side round-trip) and the
/// spec-level <c>SlackDmSessionMappingMigrationSpecs</c> (schema surface).
/// </summary>
[Collection("SlackApiSurface")]
public sealed class SlackDmSessionMappingIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackDmSessionMappingIngressSpecs(IsolatedMohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Ingress_records_dm_session_mapping_for_the_first_dm()
    {
        var connection = await CreateConnectionAsync();

        using var response = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            apiAppId = "A123",
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-MAP",
            messageTs = "1710000000.000100",
            senderSlackUserId = "U_OWNER",
            text = "first task",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
        var sessionId = await mapping.GetCurrentSessionIdAsync(
            connection.ProjectId, connection.Id, "D-DM-MAP");

        Assert.False(string.IsNullOrEmpty(sessionId));
    }

    [Theory]
    [InlineData(AgentJobFailureReasons.RunnerUnavailable)]
    [InlineData("skill-not-found")]
    public async Task Retry_safe_failed_initial_launch_recovers_and_accepts_followup_once(string failureCategory)
    {
        var connection = await CreateConnectionAsync();
        const string conversationId = "D-DM-MISSING-RUNTIME";
        const string followupTs = "1710000000.000150";

        using (var first = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            apiAppId = "A123",
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs = "1710000000.000100",
            senderSlackUserId = "U_OWNER",
            text = "first task",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        }))
        {
            first.EnsureSuccessStatusCode();
        }

        string failedSessionId;
        await using (var failScope = _fixture.Services.CreateAsyncScope())
        {
            failedSessionId = (await failScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>()
                .GetCurrentSessionIdAsync(connection.ProjectId, connection.Id, conversationId))!;
        }
        var failedSession = _fixture.Grains.GetGrain<IAgentSessionGrain>(failedSessionId);
        var failedInitial = await failedSession.GetInitialLaunchAsync();
        Assert.NotNull(failedInitial?.Turn?.JobId);
        await failedSession.MarkInitialTurnTerminalAsync(
            failedInitial!.Turn!.JobId!,
            AgentTurnStatus.Failed,
            new AgentTurnResult(
                FailureReason: "failed before execution started",
                FailureCategory: failureCategory));

        async Task<JsonElement> PostFollowupAsync(string messageTs, string text)
        {
            using var response = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
            {
                apiAppId = "A123",
                isDirectMessage = true,
                teamId = connection.WorkspaceTeamId,
                conversationId,
                messageTs,
                senderSlackUserId = "U_OWNER",
                text,
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
            var responseText = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Slack follow-up returned {(int)response.StatusCode}: {responseText}");
            using var document = JsonDocument.Parse(responseText);
            return document.RootElement.GetProperty("data").Clone();
        }

        var followups = await Task.WhenAll(
            PostFollowupAsync(followupTs, "more details"),
            PostFollowupAsync("1710000000.000151", "one more constraint"));
        var firstFollowup = followups[0];
        Assert.Equal("accepted", firstFollowup.GetProperty("kind").GetString());
        Assert.Equal("none", firstFollowup.GetProperty("responseOwner").GetString());
        var replacementSessionId = firstFollowup.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(replacementSessionId));
        Assert.NotEqual(failedSessionId, replacementSessionId);
        Assert.All(followups, followup =>
        {
            Assert.Equal("accepted", followup.GetProperty("kind").GetString());
            Assert.Equal(replacementSessionId, followup.GetProperty("sessionId").GetString());
        });

        var replay = await PostFollowupAsync(followupTs, "more details");
        Assert.Equal("already_accepted", replay.GetProperty("kind").GetString());
        Assert.Equal(replacementSessionId, replay.GetProperty("sessionId").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var deliveries = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.DispatchRef == $"slack-followup-rejected:T123/{conversationId}/{followupTs}")
            .ToListAsync();
        Assert.Empty(deliveries);
        Assert.Equal(replacementSessionId, await scope.ServiceProvider
            .GetRequiredService<SlackDmSessionMappingStore>()
            .GetCurrentSessionIdAsync(connection.ProjectId, connection.Id, conversationId));
        Assert.Equal(2, await db.AgentJobs.CountAsync(row =>
            row.State.Contains(connection.ProjectId) && row.State.Contains(connection.AgentId)));
        var replacementInputs = await _fixture.Grains.GetGrain<IAgentSessionGrain>(replacementSessionId!)
            .ListInputsAsync();
        Assert.Equal(3, replacementInputs.Count);
        Assert.Single(replacementInputs, input => input.Text == "more details");
        Assert.Single(replacementInputs, input => input.Text == "one more constraint");
    }

    [Fact]
    public async Task Ingress_redelivery_collapses_to_already_accepted()
    {
        var connection = await CreateConnectionAsync();

        using var first = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            apiAppId = "A123",
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-REPLAY",
            messageTs = "1710000000.000200",
            senderSlackUserId = "U_OWNER",
            text = "do this",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        List<string> beforeProjection;
        await using (var beforeScope = _fixture.Services.CreateAsyncScope())
        {
            var beforeDb = beforeScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            beforeProjection = await beforeDb.SlackOutboxRows
                .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "D-DM-REPLAY")
                .OrderBy(row => row.Id)
                .Select(row => row.Kind + "|" + row.DispatchRef + "|" + row.ThreadTs + "|" + row.PayloadJson)
                .ToListAsync();
        }

        using var replay = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
        {
            apiAppId = "A123",
            isDirectMessage = true,
            teamId = connection.WorkspaceTeamId,
            conversationId = "D-DM-REPLAY",
            messageTs = "1710000000.000200",
            senderSlackUserId = "U_OWNER",
            text = "do this",
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var uniqueIdentities = await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == connection.Id
                && row.ConversationId == "D-DM-REPLAY")
            .CountAsync();
        Assert.Equal(1, uniqueIdentities);
        var afterProjection = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "D-DM-REPLAY")
            .OrderBy(row => row.Id)
            .Select(row => row.Kind + "|" + row.DispatchRef + "|" + row.ThreadTs + "|" + row.PayloadJson)
            .ToListAsync();
        Assert.Equal(beforeProjection, afterProjection);
        Assert.DoesNotContain(afterProjection, row => row.Contains("xoxb-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ingress_followup_dispatches_for_legacy_idle_session_without_bound_root()
    {
        var connection = await CreateConnectionAsync();
        var sessionId = $"session-followup-{connection.Id}";
        var jobKey = $"job-followup-{connection.Id}";
        var runnerId = $"dm-followup-runner-{Guid.NewGuid():N}";
        var runnerConnectionId = $"{runnerId}-connection";
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            var now = _fixture.TimeProvider.GetUtcNow();
            var sessionState = """
                {"id":"__SESSION__","metadata":{"labels":{"mohist.io/project-id":"__PROJECT__","mohist.io/source-kind":"agent-connection","mohist.io/connection-id":"__CONNECTION__","mohist.io/slack-user-id":"U_OWNER","mohist.io/slack-conversation-id":"D-DM-FOLLOWUP","mohist.io/agent-id":"agent-1","mohist.io/agent-name":"Mohist Agent"}},"runtime":{"runnerId":"__RUNNER__","workDir":null,"runtime":"opencode"},"settings":{"model":"gpt-4o"},"status":{"agentRuntimeSessionId":"runtime-followup","activity":"idle","createdAt":"__NOW__","lastDataAt":"__NOW__","inputs":[{"id":"initial-input","sequence":1,"text":"initial","source":"agent-connection","acceptance":"accepted","recordedAt":"__NOW__","jobId":"__JOB__","provenance":{"providerKind":"slack","workspaceId":"T123","conversationId":"D-DM-FOLLOWUP","threadId":null,"memberId":"U_OWNER","messageId":"initial-message","connectionId":"__CONNECTION__"}}],"turns":[{"id":"initial-turn","sequence":1,"inputIds":["initial-input"],"status":"completed","jobId":"__JOB__","recordedAt":"__NOW__","updatedAt":"__NOW__"}]}}
                """;
            sessionState = sessionState
                .Replace("__SESSION__", sessionId)
                .Replace("__PROJECT__", connection.ProjectId)
                .Replace("__CONNECTION__", connection.Id)
                .Replace("__RUNNER__", runnerId)
                .Replace("__JOB__", jobKey)
                .Replace("__NOW__", now.UtcDateTime.ToString("O"));
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                AgentSessionId = "runtime-followup",
                RunnerId = runnerId,
                Status = "bound",
                State = sessionState,
                CreatedAt = now.UtcDateTime,
            });
            db.AgentSessionTranscriptTurns.Add(new AgentSessionTranscriptTurnRow
            {
                SessionId = sessionId,
                RuntimeSessionId = "runtime-followup",
                Sequence = 1,
                PromptText = "initial",
                PromptKind = "task",
                StartedAt = now.UtcDateTime,
                UpdatedAt = now.UtcDateTime,
            });
            db.AgentJobs.Add(new AgentJobRow
            {
                JobKey = jobKey,
                State = $"{{\"input\":{{\"projectId\":\"{connection.ProjectId}\",\"agentId\":\"agent-1\"}},\"status\":\"executing\",\"submittedAt\":\"{now:O}\"}}",
            });
            await db.SaveChangesAsync();

            await mapping.SetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER",
                "D-DM-FOLLOWUP", sessionId);
        }

        try
        {
            using (var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
            {
                processGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
                capabilities = new[] { "spec/*" },
                hostname = $"{runnerId}-host",
                projectId = connection.ProjectId,
                runtimeCatalogs = CapabilityCatalogTestHelpers.Create(),
            }))
            {
                register.EnsureSuccessStatusCode();
            }
            var runnerHub = _fixture.Services.GetRequiredService<IRunnerControlTransport>() as RecordingRunnerControlTransport
                ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
            runnerHub.Clear();
            _fixture.Services.GetRequiredService<RunnerConnectionTracker>().Register(runnerId, runnerConnectionId);

            using var followup = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
            {
                apiAppId = "A123",
                isDirectMessage = true,
                teamId = connection.WorkspaceTeamId,
                conversationId = "D-DM-FOLLOWUP",
                messageTs = "1710000000.000300",
                senderSlackUserId = "U_OWNER",
                text = "more details",
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });

            Assert.Equal(HttpStatusCode.OK, followup.StatusCode);
            using var document = JsonDocument.Parse(await followup.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.TryGetProperty("sessionId", out _),
                "follow-up path must surface the same session id without launching a new AgentJob");
            Assert.True(data.TryGetProperty("inputId", out _),
                "follow-up path must surface the SessionInput id");
            Assert.True(data.GetProperty("followup").GetBoolean());
            Assert.Single(runnerHub.SentMessages,
                message => message.ConnectionId == runnerId && message.Method == "session.followup");
            var followupTurnId = data.GetProperty("turnId").GetString()!;

            await using (var projectionScope = _fixture.Services.CreateAsyncScope())
            {
                var projectionDb = projectionScope.ServiceProvider.GetRequiredService<MohistDbContext>();
                var progress = await projectionDb.SlackOutboxRows.SingleAsync(row =>
                    row.ConnectionId == connection.Id
                    && row.ConversationId == "D-DM-FOLLOWUP"
                    && row.ThreadTs == "1710000000.000300"
                    && row.Kind == SlackOutboxKinds.ReplaceableProgress
                    && row.DispatchRef == $"agent-session-followup:{sessionId}:{followupTurnId}:progress");
                var payload = SlackDeliveryPayload.Parse(progress.PayloadJson);
                var source = new SlackMessageIdentity(
                    connection.WorkspaceTeamId,
                    "D-DM-FOLLOWUP",
                    "1710000000.000300");
                Assert.Equal(SlackStatusProjection.DispatchRef(source, "status"), payload.ClientMessageId);
                Assert.Equal(SlackStatusProjection.DispatchRef(source, "status"), payload.StatusDispatchRef);
                Assert.DoesNotContain("xoxb-", progress.PayloadJson, StringComparison.Ordinal);
            }

            var switched = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
            {
                apiAppId = "A123",
                isDirectMessage = true,
                teamId = connection.WorkspaceTeamId,
                conversationId = "D-DM-FOLLOWUP",
                messageTs = "1710000000.000400",
                senderSlackUserId = "U_OWNER",
                text = "new task separate work",
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
            switched.EnsureSuccessStatusCode();

            List<string> beforeReplayProjection;
            await using (var beforeReplayScope = _fixture.Services.CreateAsyncScope())
            {
                var beforeReplayDb = beforeReplayScope.ServiceProvider.GetRequiredService<MohistDbContext>();
                beforeReplayProjection = await beforeReplayDb.SlackOutboxRows
                    .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "D-DM-FOLLOWUP")
                    .OrderBy(row => row.Id)
                    .Select(row => row.Kind + "|" + row.DispatchRef + "|" + row.ThreadTs + "|" + row.PayloadJson)
                    .ToListAsync();
            }

            using var replay = await _fixture.Client.PostAsJsonAsync(Path(connection, "/ingress"), new
            {
                apiAppId = "A123",
                isDirectMessage = true,
                teamId = connection.WorkspaceTeamId,
                conversationId = "D-DM-FOLLOWUP",
                messageTs = "1710000000.000300",
                senderSlackUserId = "U_OWNER",
                text = "more details",
                leaseId = _connectionLeases[connection.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
            replay.EnsureSuccessStatusCode();
            using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            var replayData = replayDocument.RootElement.GetProperty("data");

            Assert.Equal(sessionId, replayData.GetProperty("sessionId").GetString());
            await using var verifyScope = _fixture.Services.CreateAsyncScope();
            var state = await verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>().AgentSessions
                .Where(row => row.Id == sessionId)
                .Select(row => row.State)
                .SingleAsync();
            Assert.Equal(2, JsonDocument.Parse(state).RootElement.GetProperty("status").GetProperty("turns").GetArrayLength());
            var afterReplayProjection = await verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>().SlackOutboxRows
                .Where(row => row.ConnectionId == connection.Id && row.ConversationId == "D-DM-FOLLOWUP")
                .OrderBy(row => row.Id)
                .Select(row => row.Kind + "|" + row.DispatchRef + "|" + row.ThreadTs + "|" + row.PayloadJson)
                .ToListAsync();
            Assert.Equal(beforeReplayProjection, afterReplayProjection);
            Assert.DoesNotContain(afterReplayProjection, row => row.Contains("xoxb-", StringComparison.Ordinal));
        }
        finally
        {
            _fixture.Services.GetRequiredService<RunnerConnectionTracker>()
                .Unregister(runnerId, runnerConnectionId);
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task Delete_connection_cascades_dm_session_mappings()
    {
        var connection = await CreateConnectionAsync();
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var mapping = scope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            await mapping.SetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, connection.WorkspaceTeamId, "U_OWNER",
                "D-DM-CASCADE", $"session-{connection.Id}");
        }

        await using (var verifyScope = _fixture.Services.CreateAsyncScope())
        {
            var mappingStore = verifyScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            Assert.Equal($"session-{connection.Id}",
                await mappingStore.GetCurrentSessionIdAsync(
                    connection.ProjectId, connection.Id, "D-DM-CASCADE"));
        }

        using var delete = await _fixture.Client.DeleteAsync(Path(connection, string.Empty));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        await using (var finalScope = _fixture.Services.CreateAsyncScope())
        {
            var mappingStore = finalScope.ServiceProvider.GetRequiredService<SlackDmSessionMappingStore>();
            Assert.Null(await mappingStore.GetCurrentSessionIdAsync(
                connection.ProjectId, connection.Id, "D-DM-CASCADE"));
        }
    }

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
                AgentConfig = JsonSerializer.SerializeToElement(new
                {
                    model = "openai/gpt-4o",
                    runtime = "opencode",
                }),
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
        await db.SaveChangesAsync();

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
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-old"));
        await secrets.StoreAsync(new SecretStoreAddress(projectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-old"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, projectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection
        {
            Id = id,
            ProjectId = projectId,
            WorkspaceTeamId = "T123",
        };
    }

    private static string Path(AgentConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}{suffix}";
}
