using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

[Collection("MohistIntegration")]
public sealed class SlackManagerAdapterSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public SlackManagerAdapterSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Manager_adapter_target_uses_its_own_discovery_lease_and_delivery_routes()
    {
        const string enrollmentId = "enrollment-manager-adapter";
        const string teamId = "T_MANAGER_ADAPTER";
        const string credentialRef = "manager-credential-adapter";
        const string managerCredential = "manager credential";
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
            {
                Id = enrollmentId,
                WorkspaceTeamId = teamId,
                Lifecycle = SlackEnrollmentLifecycle.Active,
                ManagerCapability = SlackManagerCapability.Available,
                ManagerAppId = "A_MANAGER_ADAPTER",
                ManagerBotUserId = "U_MANAGER_ADAPTER",
                ManagerCredentialRef = credentialRef,
                ManagerReadiness = SlackManagerReadiness.Ready,
                ManagerTransportKind = SlackManagerTransportKind.Socket,
                RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified,
                PlanCode = "unknown",
                AuditJson = "[]",
                CreatedAt = _fixture.TimeProvider.GetUtcNow(),
                UpdatedAt = _fixture.TimeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            await secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.AppToken),
                Encoding.UTF8.GetBytes("xapp-manager-adapter"));
            await secrets.StoreAsync(
                SecretStoreAddress.ForSlackWorkspaceEnrollment(enrollmentId, SecretKind.BotToken),
                Encoding.UTF8.GetBytes(managerCredential));
        }

        using var discovery = await _fixture.Client.GetAsync("/api/slack-manager/adapter");
        discovery.EnsureSuccessStatusCode();
        using var discoveryDocument = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        var target = Assert.Single(
            discoveryDocument.RootElement.GetProperty("data").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("enrollmentId").GetString(),
                enrollmentId,
                StringComparison.Ordinal));
        Assert.Equal("manager", target.GetProperty("ownerKind").GetString());
        Assert.Equal(enrollmentId, target.GetProperty("enrollmentId").GetString());
        Assert.Equal(teamId, target.GetProperty("workspaceTeamId").GetString());
        Assert.False(target.TryGetProperty("projectId", out _));
        Assert.False(target.TryGetProperty("connectionId", out _));

        string deliveryId;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
            deliveryId = (await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
                SlackDeliveryOwnerIds.ManagerProjectId,
                enrollmentId,
                teamId,
                "D_MANAGER_ADAPTER",
                SlackOutboxKinds.TerminalResult,
                "manager-adapter:delivery-1",
                JsonSerializer.Serialize(new SlackDeliveryPayload(
                    SlackDeliveryOperations.PostMessage,
                    "manager adapter reply")),
                OwnerKind: SlackDeliveryOwnerKinds.Manager))).Id;
        }

        var firstLease = await SlackRuntimeLeaseTestSupport.AcquireManagerLeaseAsync(
            _fixture, enrollmentId, teamId, "adapter-manager");

        using var claim = await _fixture.Client.PostAsJsonAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/claim",
            new { adapterId = "adapter-manager", leaseId = firstLease });
        claim.EnsureSuccessStatusCode();
        using var claimDocument = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        var claimed = claimDocument.RootElement.GetProperty("data");
        Assert.Equal(deliveryId, claimed.GetProperty("id").GetString());
        Assert.Equal("manager", claimed.GetProperty("ownerKind").GetString());

        using var uncertain = await _fixture.Client.PostAsJsonAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/ack",
            new { id = deliveryId, adapterId = "adapter-manager", outcome = "uncertain", reason = "provider_unknown", leaseId = firstLease });
        uncertain.EnsureSuccessStatusCode();
        using var uncertainDocument = JsonDocument.Parse(await uncertain.Content.ReadAsStringAsync());
        Assert.Equal("manager", uncertainDocument.RootElement.GetProperty("data").GetProperty("ownerKind").GetString());

        // The retry adapter takes over: a fresh lease under its own adapter
        // id supersedes the first holder, mirroring the real handover.
        var retryLease = await SlackRuntimeLeaseTestSupport.AcquireManagerLeaseAsync(
            _fixture, enrollmentId, teamId, "adapter-manager-retry");

        using var reclaim = await _fixture.Client.PostAsJsonAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/claim-uncertain",
            new { adapterId = "adapter-manager-retry", leaseId = retryLease });
        reclaim.EnsureSuccessStatusCode();
        using var reclaimDocument = JsonDocument.Parse(await reclaim.Content.ReadAsStringAsync());
        Assert.Equal(deliveryId, reclaimDocument.RootElement.GetProperty("data").GetProperty("id").GetString());

        using var delivered = await _fixture.Client.PostAsJsonAsync(
            $"/api/slack-manager/adapter/{enrollmentId}/deliveries/ack",
            new { id = deliveryId, adapterId = "adapter-manager-retry", outcome = "delivered", leaseId = retryLease });
        delivered.EnsureSuccessStatusCode();

        await using var verify = _fixture.Services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await database.SlackOutboxRows.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(SlackDeliveryOwnerIds.ManagerProjectId, row.ProjectId);
        Assert.Equal(enrollmentId, row.ConnectionId);
        Assert.Equal(SlackDeliveryOwnerKinds.Manager, row.OwnerKind);
        Assert.Equal(SlackOutboxStates.Delivered, row.State);
    }

    [Fact]
    public async Task Legacy_manager_adapter_session_route_is_removed()
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            "/api/slack-manager/adapter/enrollment-legacy/session",
            new { adapterId = "adapter-legacy" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
