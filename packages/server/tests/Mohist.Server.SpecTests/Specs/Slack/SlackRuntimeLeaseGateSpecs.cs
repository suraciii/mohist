using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
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
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// The runtime Socket lease gate: every adapter-facing route that can
/// produce an inbox/outbox side effect (ingress, interaction, delivery
/// claim / claim-uncertain / ack, manager ingress) fails closed with
/// <c>lease_stale_or_expired</c> before any side effect unless the caller
/// proves it holds the current runtime lease for the target. Runs against
/// the production EF lease store, the enrollment-backed target provider
/// and the fixed fake clock — no real network, process or wall-clock.
/// </summary>
[Collection("MohistIntegration")]
public sealed class SlackRuntimeLeaseGateSpecs
{
    private const string TeamId = "T_LEASE_GATE";
    private const string AdapterId = "adapter-gate";

    private readonly MohistIntegrationFixture _fixture;

    public SlackRuntimeLeaseGateSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Connection_ingress_claim_and_ack_require_the_current_runtime_lease()
    {
        var connection = await SeedConnectionAsync();
        var target = new { kind = SlackLeaseTargetKind.Connection, projectId = connection.ProjectId, connectionId = connection.ConnectionId };
        var firstLease = await AcquireRuntimeLeaseAsync(target);
        var deliveryId = await EnqueueConnectionDeliveryAsync(connection);

        // The current lease is accepted on every gated surface.
        using var ingressOk = await PostIngressAsync(connection, firstLease);
        Assert.Equal(HttpStatusCode.OK, ingressOk.StatusCode);
        Assert.Equal("ignored", (await DataAsync(ingressOk)).GetProperty("kind").GetString());

        using var claimOk = await PostAsync(
            IngressPath(connection, "/deliveries/claim"), new { leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.OK, claimOk.StatusCode);
        Assert.Equal(deliveryId, (await DataAsync(claimOk)).GetProperty("id").GetString());

        using var ackOk = await PostAsync(
            IngressPath(connection, "/deliveries/ack"),
            new { id = deliveryId, outcome = "delivered", leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.OK, ackOk.StatusCode);

        // A superseded lease fails closed on every gated surface.
        var superseding = await AcquireRuntimeLeaseAsync(target);
        Assert.NotEqual(firstLease, superseding);

        using var staleIngress = await PostIngressAsync(connection, firstLease);
        Assert.Equal(HttpStatusCode.Conflict, staleIngress.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(staleIngress));

        using var staleClaim = await PostAsync(
            IngressPath(connection, "/deliveries/claim"), new { leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.Conflict, staleClaim.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(staleClaim));

        using var staleAck = await PostAsync(
            IngressPath(connection, "/deliveries/ack"),
            new { id = deliveryId, outcome = "uncertain", leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.Conflict, staleAck.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(staleAck));

        // The holder of the current lease still works after the supersede.
        using var reIngress = await PostIngressAsync(connection, superseding);
        Assert.Equal(HttpStatusCode.OK, reIngress.StatusCode);
        Assert.Equal("ignored", (await DataAsync(reIngress)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task An_expired_lease_cannot_ingress_or_claim_even_without_a_supersede()
    {
        var connection = await SeedConnectionAsync();
        var target = new { kind = SlackLeaseTargetKind.Connection, projectId = connection.ProjectId, connectionId = connection.ConnectionId };
        var lease = await AcquireRuntimeLeaseAsync(target);

        // The shared fixture clock must not move (FakeTimeProvider cannot
        // rewind and the whole collection shares it): expire the lease row
        // directly. The gate reads the same production store row and
        // compares ExpiresAt, so the fail-closed path under test is unchanged.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var targetKey = new SlackLeaseTargetRef.Connection(
                connection.ProjectId, connection.ConnectionId).TargetKey;
            var row = await db.SlackAdapterLeases.SingleAsync(row =>
                row.TargetKey == targetKey && row.LeaseKind == SlackLeaseKind.Runtime);
            row.ExpiresAt = _fixture.TimeProvider.GetUtcNow() - TimeSpan.FromSeconds(1);
            await db.SaveChangesAsync();
        }

        using var expiredIngress = await PostIngressAsync(connection, lease);
        Assert.Equal(HttpStatusCode.Conflict, expiredIngress.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(expiredIngress));

        using var expiredClaim = await PostAsync(
            IngressPath(connection, "/deliveries/claim"), new { leaseId = lease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.Conflict, expiredClaim.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(expiredClaim));

        using var expiredAck = await PostAsync(
            IngressPath(connection, "/deliveries/ack"),
            new { id = "whatever", outcome = "retry", leaseId = lease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.Conflict, expiredAck.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(expiredAck));
    }

    [Fact]
    public async Task Manager_ingress_claim_and_ack_require_the_current_runtime_lease()
    {
        var enrollmentId = await SeedEnrollmentAsync();
        var target = new { kind = SlackLeaseTargetKind.Manager, enrollmentId, workspaceTeamId = TeamId };
        var firstLease = await AcquireRuntimeLeaseAsync(target);
        var deliveryId = await EnqueueManagerDeliveryAsync(enrollmentId);

        // The current lease is accepted on the manager surfaces.
        using var ingressOk = await PostAsync("/api/slack-manager/ingress", ManagerIngressBody(firstLease));
        Assert.Equal(HttpStatusCode.OK, ingressOk.StatusCode);
        Assert.Equal("rejected", (await DataAsync(ingressOk)).GetProperty("decision").GetString());

        using var claimOk = await PostAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/claim",
            new { leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.OK, claimOk.StatusCode);
        Assert.Equal(deliveryId, (await DataAsync(claimOk)).GetProperty("id").GetString());

        using var ackOk = await PostAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/ack",
            new { id = deliveryId, outcome = "delivered", leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.OK, ackOk.StatusCode);

        // A superseded lease fails closed on every gated manager surface.
        var superseding = await AcquireRuntimeLeaseAsync(target);

        using var staleIngress = await PostAsync("/api/slack-manager/ingress", ManagerIngressBody(firstLease));
        Assert.Equal(HttpStatusCode.Conflict, staleIngress.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(staleIngress));

        using var staleClaim = await PostAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/claim-uncertain",
            new { leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.Conflict, staleClaim.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(staleClaim));

        using var staleAck = await PostAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/ack",
            new { id = deliveryId, outcome = "uncertain", leaseId = firstLease, adapterId = AdapterId });
        Assert.Equal(HttpStatusCode.Conflict, staleAck.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(staleAck));

        // The current lease still works after the supersede.
        using var reIngress = await PostAsync("/api/slack-manager/ingress", ManagerIngressBody(superseding));
        Assert.Equal(HttpStatusCode.OK, reIngress.StatusCode);
    }

    [Fact]
    public async Task An_old_lease_cannot_trigger_a_Stop_interaction_and_leaves_no_side_effects()
    {
        var connection = await SeedConnectionAsync();
        var target = new { kind = SlackLeaseTargetKind.Connection, projectId = connection.ProjectId, connectionId = connection.ConnectionId };
        var oldLease = await AcquireRuntimeLeaseAsync(target);
        var currentLease = await AcquireRuntimeLeaseAsync(target);

        var body = new
        {
            eventType = "block_actions",
            interactionId = "trigger-stop",
            teamId = TeamId,
            conversationId = "C_GATE_STOP",
            messageTs = "1710000000.000999",
            actorSlackUserId = "U_GATE_OWNER",
            actionId = SlackTurnControlService.StopActionId,
            actionValue = "server-signed-value",
            leaseId = oldLease,
            adapterId = AdapterId,
        };

        var inboxBefore = await CountConnectionInboxAsync(connection);
        var outboxBefore = await CountConnectionOutboxAsync(connection);
        using var stale = await PostAsync(IngressPath(connection, "/interactions"), body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(stale));
        Assert.Equal(inboxBefore, await CountConnectionInboxAsync(connection));
        Assert.Equal(outboxBefore, await CountConnectionOutboxAsync(connection));

        // The same interaction with the current lease passes the gate (the
        // invalid signature is then rejected by turn control, not by the gate).
        using var current = await PostAsync(IngressPath(connection, "/interactions"), new
        {
            eventType = "block_actions",
            interactionId = "trigger-stop",
            teamId = TeamId,
            conversationId = "C_GATE_STOP",
            messageTs = "1710000000.000999",
            actorSlackUserId = "U_GATE_OWNER",
            actionId = SlackTurnControlService.StopActionId,
            actionValue = "server-signed-value",
            leaseId = currentLease,
            adapterId = AdapterId,
        });
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("invalid_action", (await DataAsync(current)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_missing_lease_proof_fails_closed_before_any_side_effect()
    {
        var connection = await SeedConnectionAsync();
        var deliveryId = await EnqueueConnectionDeliveryAsync(connection);

        using var noLeaseIngress = await PostAsync(IngressPath(connection, "/ingress"), new
        {
            isDirectMessage = false,
            teamId = TeamId,
            conversationId = "C_GATE",
            messageTs = "1710000000.000300",
            mentionedUserIds = new[] { "U_LEASE_GATE_BOT" },
            senderSlackUserId = "U_BOT",
            senderKind = "bot",
            text = "gate probe",
            adapterId = AdapterId,
        });
        Assert.Equal(HttpStatusCode.Conflict, noLeaseIngress.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(noLeaseIngress));

        using var noAdapterClaim = await PostAsync(
            IngressPath(connection, "/deliveries/claim"), new { leaseId = "lease-unknown" });
        Assert.Equal(HttpStatusCode.Conflict, noAdapterClaim.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(noAdapterClaim));

        using var noLeaseAck = await PostAsync(
            IngressPath(connection, "/deliveries/ack"), new { id = "x", outcome = "retry" });
        Assert.Equal(HttpStatusCode.Conflict, noLeaseAck.StatusCode);
        Assert.Equal("lease_stale_or_expired", await CodeAsync(noLeaseAck));

        // The enqueued delivery was never claimed and no inbox row was created.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Equal(0, await db.SlackProviderInboxRows.CountAsync(row =>
            row.ConnectionId == connection.ConnectionId));
        var delivery = await db.SlackOutboxRows.SingleAsync(row => row.Id == deliveryId);
        Assert.Equal(SlackOutboxStates.Pending, delivery.State);
    }

    private async Task<int> CountConnectionInboxAsync(SeededConnection connection)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.SlackProviderInboxRows.CountAsync(row =>
            row.ConnectionId == connection.ConnectionId);
    }

    private async Task<int> CountConnectionOutboxAsync(SeededConnection connection)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.SlackOutboxRows.CountAsync(row =>
            row.OwnerKind == SlackDeliveryOwnerKinds.Connection
            && row.ConnectionId == connection.ConnectionId);
    }

    private async Task<SeededConnection> SeedConnectionAsync()
    {
        var connectionId = $"connection_{Guid.NewGuid():N}";
        var projectId = $"project_{Guid.NewGuid():N}";
        var agentAppId = $"agent_app_{Guid.NewGuid():N}";
        var agentId = $"agent_{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Projects.Add(new ProjectRow { Id = projectId, Name = projectId, CreatedAt = now, UpdatedAt = now });
        await EnsureEnrollmentAsync();
        db.Agents.Add(new AgentRow
        {
            Id = agentId,
            ProjectId = projectId,
            Name = "Lease Gate Agent",
            Status = AgentStatus.Active,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = agentId,
                ProjectId = projectId,
                Name = "Lease Gate Agent",
                Status = AgentStatus.Active,
                AgentConfig = JsonSerializer.SerializeToElement(new { model = "openai/gpt-4o", runtime = "opencode" }),
            }, JSON.Options),
        });
        var appId = $"A_LEASE_GATE_{Guid.NewGuid():N}";
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = connectionId,
            ProjectId = projectId,
            AgentId = agentId,
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = TeamId,
            AppId = appId,
            BotUserId = "U_LEASE_GATE_BOT",
            BotName = "Lease Gate Bot",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            OwnerSlackUserId = "U_GATE_OWNER",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ManagedSlackAgentApps.Add(new ManagedSlackAgentAppRow
        {
            Id = agentAppId,
            EnrollmentId = "enrollment-gate",
            WorkspaceTeamId = TeamId,
            AgentConnectionId = connectionId,
            AppId = appId,
            BotUserId = "U_LEASE_GATE_BOT",
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
        await secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.AppToken),
            Encoding.UTF8.GetBytes("xapp-gate"));
        await secrets.StoreAsync(
            SecretStoreAddress.ForManagedSlackAgentApp(agentAppId, SecretKind.BotToken),
            Encoding.UTF8.GetBytes("xoxb-gate"));
        return new SeededConnection(projectId, connectionId);
    }

    private async Task<string> SeedEnrollmentAsync()
    {
        await EnsureEnrollmentAsync();
        return "enrollment-gate";
    }

    /// <summary>
    /// One active, verified enrollment per team (the team is unique per
    /// fixture): created once, reused by every seed in this spec class.
    /// </summary>
    private async Task EnsureEnrollmentAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var existing = await db.SlackWorkspaceEnrollments.FirstOrDefaultAsync(row => row.WorkspaceTeamId == TeamId);
        if (existing is not null)
            return;
        var now = _fixture.TimeProvider.GetUtcNow();
        db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
        {
            Id = "enrollment-gate",
            WorkspaceTeamId = TeamId,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            ManagerAppId = "A_LEASE_GATE_MANAGER",
            ManagerBotUserId = "U_LEASE_GATE_MANAGER",
            ManagerCredentialRef = "enrollment-gate",
            ManagerReadiness = SlackManagerReadiness.Ready,
            ManagerTransportKind = SlackManagerTransportKind.Socket,
            RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
            PlanCode = "unknown",
            AuditJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-gate", SecretKind.AppToken),
            Encoding.UTF8.GetBytes("xapp-gate-manager"));
        await secrets.StoreAsync(
            SecretStoreAddress.ForSlackWorkspaceEnrollment("enrollment-gate", SecretKind.BotToken),
            Encoding.UTF8.GetBytes("xoxb-gate-manager"));
    }

    private async Task<string> EnqueueConnectionDeliveryAsync(SeededConnection connection)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var result = await outbox.EnqueueAsync(new SlackOutboxDraft(
            connection.ProjectId,
            connection.ConnectionId,
            TeamId,
            "C_GATE",
            SlackOutboxKinds.TerminalResult,
            "gate:delivery:connection",
            JsonSerializer.Serialize(new { text = "gate reply" })));
        return result.Id;
    }

    private async Task<string> EnqueueManagerDeliveryAsync(string enrollmentId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
        var result = await outbox.EnqueueAsync(new SlackOutboxDraft(
            SlackDeliveryOwnerIds.ManagerProjectId,
            enrollmentId,
            TeamId,
            "C_GATE_MANAGER",
            SlackOutboxKinds.TerminalResult,
            "gate:delivery:manager",
            JsonSerializer.Serialize(new { text = "manager gate reply" }),
            OwnerKind: SlackDeliveryOwnerKinds.Manager));
        return result.Id;
    }

    private async Task<string> AcquireRuntimeLeaseAsync(object target)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/slack-adapter/leases/acquire", new
        {
            kind = SlackLeaseKind.Runtime,
            target,
            adapterId = AdapterId,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("leaseId").GetString()!;
    }

    private async Task<HttpResponseMessage> PostIngressAsync(SeededConnection connection, string leaseId) =>
        await PostAsync(IngressPath(connection, "/ingress"), new
        {
            isDirectMessage = false,
            teamId = TeamId,
            conversationId = "C_GATE",
            messageTs = "1710000000.000100",
            mentionedUserIds = new[] { "U_LEASE_GATE_BOT" },
            senderSlackUserId = "U_BOT",
            senderKind = "bot",
            text = "gate probe",
            leaseId,
            adapterId = AdapterId,
        });

    private static object ManagerIngressBody(string leaseId) => new
    {
        appId = "A_LEASE_GATE_MANAGER",
        workspaceTeamId = TeamId,
        conversationId = "C_GATE_MANAGER",
        messageTs = "1710000000.000200",
        senderSlackUserId = "U_GATE_OWNER",
        text = "gate probe",
        isDirectMessage = false,
        threadTs = (string?)null,
        leaseId,
        adapterId = AdapterId,
    };

    private static string IngressPath(SeededConnection connection, string suffix) =>
        $"/api/projects/{connection.ProjectId}/slack-connections/{connection.ConnectionId}{suffix}";

    private async Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var response = await _fixture.Client.PostAsJsonAsync(path, body);
        return response;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed record SeededConnection(string ProjectId, string ConnectionId);
}
