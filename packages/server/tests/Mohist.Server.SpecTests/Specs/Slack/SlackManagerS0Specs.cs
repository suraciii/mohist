using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackManagerS0Specs
{
    private static readonly DateTimeOffset Start = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Manager_claim_is_single_use_and_expires_without_storing_code()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var factory = new TestDbContextFactory(database.Options);
        var enrollments = new SlackWorkspaceEnrollmentStore(factory, time);
        var enrollment = new SlackWorkspaceEnrollment
        {
            Id = "enrollment-manager-claim",
            WorkspaceTeamId = "T_MANAGER_CLAIM",
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            ManagerActorId = "manager-actor",
            PlanCode = "unknown",
        };
        enrollment.ConfigureManagerApp(
            "A_MANAGER",
            "U_MANAGER",
            "manager-credential-ref",
            SlackManagerTransportKind.Socket,
            SlackManagerReadiness.Ready,
            Start);
        await enrollments.CreateAsync(enrollment);
        var claims = new ManagerClaimService(enrollments, time);

        var issued = await claims.IssueAsync(enrollment.Id);
        Assert.NotNull(issued.Code);
        time.Advance(TimeSpan.FromMinutes(11));
        var expired = await claims.ConsumeAsync("T_MANAGER_CLAIM", "U_OWNER", issued.Code!);

        var replacement = await claims.IssueAsync(enrollment.Id);
        Assert.NotNull(replacement.Code);
        Assert.NotEqual(issued.Code, replacement.Code);
        var accepted = await claims.ConsumeAsync("T_MANAGER_CLAIM", "U_OWNER", replacement.Code!);
        var consumed = await claims.ConsumeAsync("T_MANAGER_CLAIM", "U_OTHER", replacement.Code!);

        Assert.Equal(SlackManagerClaimOutcome.Accepted, accepted.Outcome);
        Assert.Equal("U_OWNER", accepted.SlackUserId);
        Assert.Equal(SlackManagerClaimOutcome.Consumed, consumed.Outcome);
        Assert.DoesNotContain(issued.Code, (await enrollments.GetAsync(enrollment.Id))!.AuditJson);
        Assert.Equal(SlackManagerClaimOutcome.Expired, expired.Outcome);

        var afterClaim = await claims.IssueAsync(enrollment.Id);
        Assert.Null(afterClaim.Code);
    }

    [Fact]
    public async Task Concurrent_manager_claim_consumption_has_one_database_winner()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var factory = new TestDbContextFactory(database.Options);
        var enrollments = new SlackWorkspaceEnrollmentStore(factory, time);
        await enrollments.CreateAsync(CreateClaimEnrollment("enrollment-manager-concurrent", "T_MANAGER_CONCURRENT"));
        var claims = new ManagerClaimService(enrollments, time);
        var issued = await claims.IssueAsync("enrollment-manager-concurrent");
        Assert.NotNull(issued.Code);

        var outcomes = await Task.WhenAll(
            claims.ConsumeAsync("T_MANAGER_CONCURRENT", "U_FIRST", issued.Code!),
            claims.ConsumeAsync("T_MANAGER_CONCURRENT", "U_SECOND", issued.Code!));

        Assert.Single(outcomes, outcome => outcome.Outcome == SlackManagerClaimOutcome.Accepted);
        Assert.Single(outcomes, outcome => outcome.Outcome == SlackManagerClaimOutcome.Consumed);
        var enrollment = await enrollments.GetAsync("enrollment-manager-concurrent");
        Assert.NotNull(enrollment);
        Assert.Contains(enrollment!.ClaimedSlackUserId, new[] { "U_FIRST", "U_SECOND" });
    }

    [Theory]
    [InlineData("xapp-token")]
    [InlineData("xoxb-token")]
    [InlineData("xoxe-token")]
    [InlineData("xoxp-token")]
    [InlineData("xoxs-token")]
    public void Manager_credential_reference_rejects_slack_token_literals(string token)
    {
        var enrollment = CreateClaimEnrollment("enrollment-manager-token", "T_MANAGER_TOKEN");

        Assert.Throws<ArgumentException>(() => enrollment.ConfigureManagerApp(
            "A_MANAGER",
            "U_MANAGER",
            token,
            SlackManagerTransportKind.Socket,
            SlackManagerReadiness.Ready,
            Start));
    }

    [Fact]
    public async Task Manager_outbox_has_its_own_owner_and_recovers_uncertain_delivery()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        var time = new FakeTimeProvider(Start);
        var factory = new TestDbContextFactory(database.Options);
        await using (var db = factory.CreateDbContext())
        {
            db.SlackWorkspaceEnrollments.Add(new SlackWorkspaceEnrollmentRow
            {
                Id = "enrollment-manager-outbox",
                WorkspaceTeamId = "T_MANAGER_OUTBOX",
                Lifecycle = SlackEnrollmentLifecycle.Active,
                ManagerCapability = SlackManagerCapability.Available,
                ManagerAppId = "A_MANAGER",
                ManagerBotUserId = "U_MANAGER",
                ManagerActorId = "manager-actor",
                ManagerCredentialRef = "manager-credential-ref",
                ManagerReadiness = SlackManagerReadiness.Ready,
                PlanCode = "unknown",
                AuditJson = "[]",
                CreatedAt = Start,
                UpdatedAt = Start,
            });
            await db.SaveChangesAsync();
        }

        var outbox = new SlackOutboxStore(
            factory,
            new NoopHealthBackpressurer(),
            time,
            Options.Create(new SlackProviderOptions { OutboxCapacityPerConnection = 1 }));
        var payload = System.Text.Json.JsonSerializer.Serialize(new SlackDeliveryPayload(
            SlackDeliveryOperations.PostMessage,
            "manager response"));
        var draft = new SlackOutboxDraft(
            SlackDeliveryOwnerIds.ManagerProjectId,
            "enrollment-manager-outbox",
            "T_MANAGER_OUTBOX",
            "D_MANAGER",
            SlackOutboxKinds.TerminalResult,
            "manager:dispatch-1",
            payload,
            OwnerKind: SlackDeliveryOwnerKinds.Manager);

        var first = await outbox.EnqueueRequiredAsync(draft);
        var duplicate = await outbox.EnqueueRequiredAsync(draft);
        var claimed = await outbox.ClaimAsync(
            SlackDeliveryOwnerIds.ManagerProjectId,
            "enrollment-manager-outbox",
            "adapter",
            ownerKind: SlackDeliveryOwnerKinds.Manager);
        await outbox.MarkDeliveryUncertainAsync(
            SlackDeliveryOwnerIds.ManagerProjectId,
            first.Id,
            "transport_unknown",
            "adapter");
        var recovered = await outbox.ClaimUncertainAsync(
            SlackDeliveryOwnerIds.ManagerProjectId,
            "enrollment-manager-outbox",
            "adapter-retry",
            ownerKind: SlackDeliveryOwnerKinds.Manager);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(SlackDeliveryOwnerKinds.Manager, claimed!.OwnerKind);
        Assert.Equal(SlackOutboxStates.Claimed, recovered!.State);
        Assert.Equal("adapter-retry", recovered.ClaimedByAdapterId);
        Assert.Equal(SlackDeliveryOwnerKinds.Manager,
            Assert.Single((await outbox.ListManagerAsync("enrollment-manager-outbox")).Entries).OwnerKind);
    }

    private sealed class NoopHealthBackpressurer : ISlackConnectionHealthBackpressurer
    {
        public Task FlipBackpressuredAsync(string projectId, string connectionId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> RecoverBackpressuredAsync(string projectId, string connectionId, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private static SlackWorkspaceEnrollment CreateClaimEnrollment(string id, string workspaceTeamId)
    {
        var enrollment = new SlackWorkspaceEnrollment
        {
            Id = id,
            WorkspaceTeamId = workspaceTeamId,
            Lifecycle = SlackEnrollmentLifecycle.Active,
            ManagerCapability = SlackManagerCapability.Available,
            ManagerActorId = "manager-actor",
            PlanCode = "unknown",
        };
        enrollment.ConfigureManagerApp(
            "A_MANAGER",
            "U_MANAGER",
            "manager-credential-ref",
            SlackManagerTransportKind.Socket,
            SlackManagerReadiness.Ready,
            Start);
        return enrollment;
    }
}
