using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackMultiAgentIngressSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly Dictionary<string, string> _connectionLeases = new(StringComparer.Ordinal);

    public SlackMultiAgentIngressSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Multi_bot_mention_starts_no_work_and_prompts_once()
    {
        var connectionA = await CreateConnectionAsync("agent-A", "T-multi", "U_BOT_A", "A_BOT_A");
        var connectionB = await CreateConnectionAsync("agent-B", "T-multi", "U_BOT_B", "A_BOT_B");

        var body = new
        {
            isDirectMessage = false,
            teamId = connectionA.WorkspaceTeamId,
            conversationId = "C-multi-bot",
            messageTs = "1710000000.010100",
            threadTs = (string?)null,
            mentionedUserIds = new[] { connectionA.BotUserId, connectionB.BotUserId },
            senderSlackUserId = connectionA.OwnerSlackUserId,
            senderKind = "human",
            text = $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> who answers?",
            leaseId = _connectionLeases[connectionA.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };

        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connectionA), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ambiguous", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionA.Id || row.LabelConnectionId == connectionB.Id)
            .ToListAsync());
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.ConversationId == "C-multi-bot")
            .ToListAsync());
        Assert.Empty(await db.SlackThreadSessionMappings
            .Where(row => row.ConversationId == "C-multi-bot")
            .ToListAsync());
    }

    [Fact]
    public async Task Signed_selection_route_launches_only_the_chosen_cross_project_agent()
    {
        const string owner = "U_SELECTION_OWNER";
        var promptOwner = await CreateConnectionAsync("route-owner", "T-selection-route", owner, "A_SELECTION_OWNER");
        var selected = await CreateConnectionAsync("route-selected", "T-selection-route", owner, "A_SELECTION_SELECTED");
        var identity = new SlackMessageIdentity(
            promptOwner.WorkspaceTeamId,
            "C-selection-route",
            "1710000000.020000");

        var ingress = await PostChannelAsync(
            promptOwner,
            identity.ConversationId,
            identity.MessageTs,
            threadTs: null,
            mentions: [promptOwner.BotUserId!, selected.BotUserId!],
            text: $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> route the original task",
            senderSlackUserId: owner);
        Assert.Equal("ambiguous", ingress.GetProperty("kind").GetString());

        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner,
            identity,
            selected.ProjectId,
            selected.Id,
            chooserMessageTs: "1710000000.020001");
        var action = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("accepted", action.GetProperty("state").GetString());

        await using var scope = _fixture.Services.CreateAsyncScope();
        var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await prompts.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        Assert.Equal(selected.ProjectId, claim.ChosenProjectId);
        Assert.Equal(selected.Id, claim.ChosenConnectionId);

        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.LabelConnectionId == selected.Id)
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == promptOwner.Id)
            .ToListAsync());
        Assert.Single(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == selected.Id
                && row.SlackMessageIdentity.EndsWith(identity.MessageTs))
            .ToListAsync());
    }

    [Fact]
    public async Task Concurrent_different_selection_clicks_commit_one_winner_and_one_execution()
    {
        const string owner = "U_SELECTION_RACE";
        var connectionA = await CreateConnectionAsync("race-a", "T-selection-race", owner, "A_SELECTION_RACE_A");
        var connectionB = await CreateConnectionAsync("race-b", "T-selection-race", owner, "A_SELECTION_RACE_B");
        var identity = new SlackMessageIdentity(
            connectionA.WorkspaceTeamId,
            "C-selection-race",
            "1710000000.020010");
        await PostChannelAsync(
            connectionA,
            identity.ConversationId,
            identity.MessageTs,
            null,
            [connectionA.BotUserId!, connectionB.BotUserId!],
            $"<@{connectionA.BotUserId}> <@{connectionB.BotUserId}> choose once",
            owner);

        var choices = await DeliverChooserAndGetChoicesAsync(
            connectionA,
            identity,
            chooserMessageTs: "1710000000.020011");
        Assert.Equal(2, choices.Count);
        var results = await Task.WhenAll(choices.Select(choice =>
            PostSelectionAsync(connectionA, choice, owner)));
        Assert.Contains(results, result => result.GetProperty("state").GetString() == "accepted");
        Assert.All(results, result => Assert.Contains(
            result.GetProperty("state").GetString(),
            new[] { "accepted", "decided" }));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.LabelConnectionId == connectionA.Id
                || row.LabelConnectionId == connectionB.Id)
            .ToListAsync());
        Assert.Single(await db.SlackProviderInboxRows
            .Where(row => (row.ConnectionId == connectionA.Id || row.ConnectionId == connectionB.Id)
                && row.SlackMessageIdentity.EndsWith(identity.MessageTs))
            .ToListAsync());
    }

    [Fact]
    public async Task Followup_selection_revalidates_executability_before_committing()
    {
        const string owner = "U_SELECTION_EXEC";
        var promptOwner = await CreateConnectionAsync("exec-owner", "T-selection-exec", owner, "A_SELECTION_EXEC_OWNER");
        var selected = await CreateConnectionAsync("exec-selected", "T-selection-exec", owner, "A_SELECTION_EXEC_SELECTED");
        const string conversationId = "C-selection-exec";
        const string threadTs = "1710000000.020020";
        const string messageTs = "1710000000.020021";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>()
                .UpsertAsync(
                    selected.ProjectId,
                    selected.WorkspaceTeamId!,
                    selected.Id,
                    conversationId,
                    threadTs,
                    owner,
                    "missing-session-before-admission",
                    threadTs);
        }
        await PostChannelAsync(
            promptOwner,
            conversationId,
            messageTs,
            threadTs,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> continue only if executable",
            owner);
        var identity = new SlackMessageIdentity(promptOwner.WorkspaceTeamId!, conversationId, messageTs);
        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner,
            identity,
            selected.ProjectId,
            selected.Id,
            chooserMessageTs: "1710000000.020022",
            threadTs);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var row = await db.Agents.SingleAsync(agent =>
                agent.ProjectId == selected.ProjectId && agent.Id == selected.AgentId);
            var agent = AgentStore.Deserialize(row.State)!;
            agent.AgentConfig = null;
            row.State = AgentStore.Serialize(agent);
            await db.SaveChangesAsync();
        }

        var result = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("agent_not_configured", result.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(identity, selected);
    }

    [Fact]
    public async Task Dangling_followup_mapping_is_rejected_before_selection_commit()
    {
        const string owner = "U_SELECTION_STALE";
        var promptOwner = await CreateConnectionAsync("stale-owner", "T-selection-stale", owner, "A_SELECTION_STALE_OWNER");
        var selected = await CreateConnectionAsync("stale-selected", "T-selection-stale", owner, "A_SELECTION_STALE_SELECTED");
        const string conversationId = "C-selection-stale";
        const string threadTs = "1710000000.020030";
        const string messageTs = "1710000000.020031";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SlackThreadSessionMappingStore>()
                .UpsertAsync(
                    selected.ProjectId,
                    selected.WorkspaceTeamId!,
                    selected.Id,
                    conversationId,
                    threadTs,
                    owner,
                    "missing-selected-session",
                    threadTs);
        }
        await PostChannelAsync(
            promptOwner,
            conversationId,
            messageTs,
            threadTs,
            [promptOwner.BotUserId!, selected.BotUserId!],
            $"<@{promptOwner.BotUserId}> <@{selected.BotUserId}> stale target",
            owner);
        var identity = new SlackMessageIdentity(promptOwner.WorkspaceTeamId!, conversationId, messageTs);
        var choice = await DeliverChooserAndGetChoiceAsync(
            promptOwner,
            identity,
            selected.ProjectId,
            selected.Id,
            chooserMessageTs: "1710000000.020032",
            threadTs);

        var result = await PostSelectionAsync(promptOwner, choice, owner);
        Assert.Equal("no_longer_valid", result.GetProperty("state").GetString());
        await AssertPendingWithoutOriginalResourcesAsync(identity, selected);
    }

    [Fact]
    public async Task Noninteractive_fallback_and_nonowner_guidance_expire_without_second_message()
    {
        const string owner = "U_SELECTION_FALLBACK";
        var connections = new List<AgentConnection>();
        for (var index = 0; index < 6; index++)
        {
            connections.Add(await CreateConnectionAsync(
                $"fallback-{index}",
                "T-selection-fallback",
                owner,
                $"A_SELECTION_FALLBACK_{index}"));
        }
        var fallbackIdentity = new SlackMessageIdentity(
            "T-selection-fallback",
            "C-selection-fallback",
            "1710000000.020040");
        await PostChannelAsync(
            connections[0],
            fallbackIdentity.ConversationId,
            fallbackIdentity.MessageTs,
            null,
            connections.Select(connection => connection.BotUserId!).ToArray(),
            string.Join(' ', connections.Select(connection => $"<@{connection.BotUserId}>")) + " too many",
            owner);

        var nonOwnerA = await CreateConnectionAsync("nonowner-a", "T-selection-nonowner", "U_OWNER_A", "A_SELECTION_NONOWNER_A");
        var nonOwnerB = await CreateConnectionAsync("nonowner-b", "T-selection-nonowner", "U_OWNER_B", "A_SELECTION_NONOWNER_B");
        var nonOwnerIdentity = new SlackMessageIdentity(
            "T-selection-nonowner",
            "C-selection-nonowner",
            "1710000000.020041");
        await PostChannelAsync(
            nonOwnerA,
            nonOwnerIdentity.ConversationId,
            nonOwnerIdentity.MessageTs,
            null,
            [nonOwnerA.BotUserId!, nonOwnerB.BotUserId!],
            $"<@{nonOwnerA.BotUserId}> <@{nonOwnerB.BotUserId}> unauthorized",
            "U_INTRUDER");

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        await NewSelectionWorker().ProcessPendingAsync();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        foreach (var identity in new[] { fallbackIdentity, nonOwnerIdentity })
        {
            var rows = await db.SlackOutboxRows
                .Where(row => row.WorkspaceTeamId == identity.WorkspaceTeamId
                    && row.ConversationId == identity.ConversationId)
                .ToListAsync();
            Assert.Single(rows);
            Assert.DoesNotContain(rows, row => row.DispatchRef ==
                SlackAmbiguousPromptStore.SettlementDispatchRef(
                    identity.WorkspaceTeamId,
                    identity.ConversationId,
                    identity.MessageTs));
        }
    }

    [Fact]
    public async Task Selection_obligation_worker_recovers_cross_project_root_once()
    {
        var promptOwner = await CreateConnectionAsync("recovery-owner", "T-recovery", "U_RECOVERY", "A_RECOVERY");
        var selected = await CreateConnectionAsync("recovery-selected", "T-recovery", "U_SELECTED", "B_RECOVERY");
        var identity = new SlackMessageIdentity("T-recovery", "C-recovery", "1710000000.020100");
        var candidates = new[]
        {
            new SlackSelectionCandidateReference(promptOwner.ProjectId, promptOwner.Id, promptOwner.BotUserId),
            new SlackSelectionCandidateReference(selected.ProjectId, selected.Id, selected.BotUserId),
        };

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            await prompts.TryClaimAsync(
                promptOwner.ProjectId,
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                threadTs: null,
                promptOwner.Id,
                candidates,
                "U_RECOVERY",
                "recover in the selected project",
                "[]",
                SlackAmbiguityKinds.RootMultiMention);
            var ids = SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity);
            await prompts.TryDecideAsync(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs,
                selected.ProjectId,
                selected.Id,
                SlackSelectionDispatchKinds.RootLaunch,
                ids.SessionId,
                ids.InputId,
                ids.TurnId);
        }

        var worker = new SlackAgentSelectionObligationWorker(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            _fixture.TimeProvider,
            Options.Create(new SlackProviderOptions()),
            NullLogger<SlackAgentSelectionObligationWorker>.Instance);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await worker.ProcessPendingAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await worker.ProcessPendingAsync();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var promptsAfter = verify.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await promptsAfter.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Completed, claim!.SelectionState);
        Assert.Equal(selected.ProjectId, claim.ChosenProjectId);
        Assert.Equal(selected.Id, claim.ChosenConnectionId);
        Assert.Equal(
            SlackChannelLaunchService.PreMintSlackLaunchIds(selected.ProjectId, identity).SessionId,
            claim.SelectionSessionId);

        var db = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Single(await db.AgentSessions
            .Where(row => row.Id == claim.SelectionSessionId)
            .ToListAsync());
        Assert.Empty(await db.AgentSessions
            .Where(row => row.LabelConnectionId == promptOwner.Id)
            .ToListAsync());
    }

    private async Task<IReadOnlyList<SlackSelectionActionPayload>> DeliverChooserAndGetChoicesAsync(
        AgentConnection promptOwner,
        SlackMessageIdentity identity,
        string chooserMessageTs)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var chooser = await outbox.FindByDispatchRefAsync(
            promptOwner.ProjectId,
            promptOwner.Id,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.PromptDispatchRef(
                identity.WorkspaceTeamId,
                identity.ConversationId,
                identity.MessageTs));
        Assert.NotNull(chooser);
        await outbox.MarkDeliveredAsync(
            promptOwner.ProjectId,
            chooser!.Id,
            new SlackProviderMessageIdentity(identity.ConversationId, chooserMessageTs));

        var blocks = SlackDeliveryPayload.Parse(chooser.PayloadJson).Blocks;
        Assert.NotNull(blocks);
        return blocks!.Value.EnumerateArray()
            .SelectMany(block => block.GetProperty("elements").EnumerateArray())
            .Select(button => JSON.Deserialize<SlackSelectionActionPayload>(
                button.GetProperty("value").GetString()!)!)
            .ToArray();
    }

    private async Task<SlackSelectionActionPayload> DeliverChooserAndGetChoiceAsync(
        AgentConnection promptOwner,
        SlackMessageIdentity identity,
        string selectedProjectId,
        string selectedConnectionId,
        string chooserMessageTs,
        string? threadTs = null)
    {
        var choices = await DeliverChooserAndGetChoicesAsync(
            promptOwner,
            identity,
            chooserMessageTs);
        var choice = Assert.Single(choices, candidate =>
            string.Equals(candidate.ChosenProjectId, selectedProjectId, StringComparison.Ordinal)
            && string.Equals(candidate.ChosenConnectionId, selectedConnectionId, StringComparison.Ordinal));
        Assert.Equal(threadTs, choice.ThreadTs);
        return choice;
    }

    private async Task<JsonElement> PostSelectionAsync(
        AgentConnection promptOwner,
        SlackSelectionActionPayload choice,
        string actorSlackUserId)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{promptOwner.ProjectId}/slack-connections/{promptOwner.Id}/interactions",
            new
            {
                eventType = "block_actions",
                interactionId = $"selection-{Guid.NewGuid():N}",
                teamId = choice.WorkspaceTeamId,
                conversationId = choice.ConversationId,
                messageTs = await ChooserMessageTsAsync(promptOwner, choice),
                threadTs = choice.ThreadTs,
                actorSlackUserId,
                actionId = SlackSelectionActionPayload.ActionId,
                actionValue = JSON.Serialize(choice),
                leaseId = _connectionLeases[promptOwner.Id],
                adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
            });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<string> ChooserMessageTsAsync(
        AgentConnection promptOwner,
        SlackSelectionActionPayload choice)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var chooser = await outbox.FindByDispatchRefAsync(
            promptOwner.ProjectId,
            promptOwner.Id,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.PromptDispatchRef(
                choice.WorkspaceTeamId,
                choice.ConversationId,
                choice.OriginalMessageTs));
        Assert.NotNull(chooser);
        var providerIdentity = SlackDeliveryPayload.Parse(chooser!.PayloadJson).ProviderMessageIdentity;
        Assert.NotNull(providerIdentity);
        return providerIdentity!.Value.MessageTs;
    }

    private async Task AssertPendingWithoutOriginalResourcesAsync(
        SlackMessageIdentity identity,
        AgentConnection selected)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
        var claim = await prompts.FindAsync(
            identity.WorkspaceTeamId,
            identity.ConversationId,
            identity.MessageTs);
        Assert.NotNull(claim);
        Assert.Equal(SlackSelectionStates.Pending, claim!.SelectionState);
        Assert.Null(claim.ChosenConnectionId);

        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.SlackProviderInboxRows
            .Where(row => row.ConnectionId == selected.Id
                && row.SlackMessageIdentity.EndsWith(identity.MessageTs))
            .ToListAsync());
    }

    private SlackAgentSelectionObligationWorker NewSelectionWorker() => new(
        _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
        _fixture.TimeProvider,
        Options.Create(new SlackProviderOptions()),
        NullLogger<SlackAgentSelectionObligationWorker>.Instance);

    private async Task<JsonElement> PostChannelAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null)
    {
        var result = await PostChannelAttemptAsync(
            connection,
            conversationId,
            messageTs,
            threadTs,
            mentions,
            text,
            senderSlackUserId);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        return result.Data!.Value;
    }

    private async Task<(HttpStatusCode Status, JsonElement? Data)> PostChannelAttemptAsync(
        AgentConnection connection,
        string conversationId,
        string messageTs,
        string? threadTs,
        string[] mentions,
        string text,
        string? senderSlackUserId = null)
    {
        var body = new
        {
            isDirectMessage = false,
            teamId = connection.WorkspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            mentionedUserIds = mentions,
            senderSlackUserId = senderSlackUserId ?? connection.OwnerSlackUserId ?? "U_OWNER",
            senderKind = "human",
            text,
            leaseId = _connectionLeases[connection.Id],
            adapterId = SlackRuntimeLeaseTestSupport.AdapterId,
        };
        using var response = await _fixture.Client.PostAsJsonAsync(IngressPath(connection), body);
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null);
        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return (response.StatusCode, null);
        var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.GetProperty("data").Clone());
    }

    private async Task<AgentConnection> CreateConnectionAsync(
        string agentNameSuffix,
        string workspaceTeamId,
        string ownerSlackUserId,
        string appId,
        string? projectId = null)
    {
        var id = $"connection_{Guid.NewGuid():N}";
        var resolvedProjectId = projectId ?? $"project_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var existingProject = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == resolvedProjectId);
        if (existingProject is null)
        {
            db.Projects.Add(new ProjectRow
            {
                Id = resolvedProjectId,
                Name = resolvedProjectId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        var botUserId = $"U{agentNameSuffix.GetHashCode():X}".PadRight(8, '0').Substring(0, 8);
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = resolvedProjectId,
            Name = $"Mohist Agent {agentNameSuffix}",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = resolvedProjectId,
                Name = $"Mohist Agent {agentNameSuffix}",
                Status = AgentStatus.Active,
                Instructions = "Handle Slack requests.",
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = id,
            ProjectId = resolvedProjectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            BotUserId = botUserId,
            BotName = $"Mohist {agentNameSuffix}".Trim(),
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = ownerSlackUserId,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        var enrollmentId = await SlackRuntimeLeaseTestSupport.EnsureEnrollmentAsync(_fixture, workspaceTeamId);
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = enrollmentId,
            WorkspaceTeamId = workspaceTeamId,
            AgentConnectionId = id,
            AppId = appId,
            BotUserId = botUserId,
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
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(new SecretStoreAddress(resolvedProjectId, id, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp"));
        await secrets.StoreAsync(SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb"));
        var leaseId = await SlackRuntimeLeaseTestSupport.AcquireConnectionLeaseAsync(_fixture, resolvedProjectId, id);
        _connectionLeases[id] = leaseId;
        return new AgentConnection
        {
            Id = id,
            ProjectId = resolvedProjectId,
            AgentId = agentId,
            WorkspaceTeamId = workspaceTeamId,
            AppId = appId,
            BotUserId = botUserId,
            OwnerSlackUserId = ownerSlackUserId,
        };
    }

    private static string IngressPath(AgentConnection connection) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.Id}/ingress";
}
