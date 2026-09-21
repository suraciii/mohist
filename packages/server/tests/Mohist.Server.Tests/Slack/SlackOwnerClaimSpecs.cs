using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// The Owner claim is a separate completion fact: one short-lived, single-use
/// code per Connection and claim kind, stored only as a hash, issued by an
/// explicit action and superseded only by another explicit issue. Reading
/// installation progress never issues or invalidates one.
/// </summary>
[Trait("level", "L0")]
public sealed class SlackOwnerClaimSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
    private const string ProjectId = "project-claim";
    private const string ConnectionId = "connection-claim";
    private const string TeamId = "T_CLAIM";

    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly TestDbContextFactory _factory;
    private readonly FakeSlackMemberIdentityPort _memberIdentity = new();
    private readonly SlackOwnerClaimService _claims;

    public SlackOwnerClaimSpecs()
    {
        _factory = new TestDbContextFactory(_database.Options);
        // By default the sender is a current full member of the claim team.
        _memberIdentity.MemberResult = new SlackMemberIdentityResult(
            Confirmed: true, UserId: "U_OWNER", TeamId: TeamId);
        _claims = new SlackOwnerClaimService(
            _factory,
            _time,
            new SlackConnectionAccessDecider(new NoAllowedMemberStore(), _memberIdentity));
    }

    // A live lease that proves a verified Bot token for the member lookup.
    private SlackLeaseContext Lease() => new(
        "operator", "lease-1", "adapter-1",
        (_, _) => Task.FromResult<string?>("xoxb-fake"));

    [Fact]
    public async Task Explicit_issue_stores_only_a_hash_and_keeps_one_outstanding_code()
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);

        var issued = await _claims.GenerateAsync(ProjectId, ConnectionId);

        Assert.Equal(10, issued.Value.Length);
        Assert.Equal(FixedNow.AddMinutes(10), issued.ExpiresAt);
        await using var db = _factory.CreateDbContext();
        var rows = await db.SlackOwnerClaimCodes.ToListAsync();
        var row = Assert.Single(rows);
        Assert.NotEqual(issued.Value, row.CodeHash, StringComparer.Ordinal);
        Assert.Null(row.UsedAt);
        Assert.Null(row.SupersededBy);
        Assert.Equal(SlackOwnerClaimCodeKinds.Initial, row.Kind);
    }

    [Fact]
    public async Task Regeneration_supersedes_the_outstanding_code_so_only_the_new_one_claims()
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);
        var first = await _claims.GenerateAsync(ProjectId, ConnectionId);

        var second = await _claims.GenerateAsync(ProjectId, ConnectionId);

        Assert.Equal(
            SlackInboundDecisionKind.Rejected,
            (await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(first.Value), Lease())).Kind);
        Assert.Equal(
            SlackInboundDecisionKind.Claimed,
            (await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(second.Value), Lease())).Kind);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.SlackOwnerClaimCodes.CountAsync());
        Assert.Equal(1, await db.SlackOwnerClaimCodes.CountAsync(row => row.UsedAt != null));
    }

    [Fact]
    public async Task A_claim_code_is_single_use()
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);
        var issued = await _claims.GenerateAsync(ProjectId, ConnectionId);

        Assert.Equal(
            SlackInboundDecisionKind.Claimed,
            (await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease())).Kind);
        var replay = await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease());

        Assert.Equal(SlackInboundDecisionKind.Rejected, replay.Kind);
        Assert.Contains("no longer valid", replay.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_code_is_rejected_against_injected_time()
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);
        var issued = await _claims.GenerateAsync(ProjectId, ConnectionId);

        _time.Advance(TimeSpan.FromMinutes(11));
        var expired = await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease());

        Assert.Equal(SlackInboundDecisionKind.Rejected, expired.Kind);
        Assert.Contains("expired", expired.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_installation_progress_neither_issues_nor_invalidates_a_code()
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);
        var issued = await _claims.GenerateAsync(ProjectId, ConnectionId);

        // A status read and a rerun of the guide are observations: the
        // outstanding code stays claimable while other work proceeds.
        var rerun = await ReadConnectionAsync();

        Assert.Equal(SlackOwnerClaimCodeKinds.Initial, rerun);
        Assert.Equal(
            SlackInboundDecisionKind.Claimed,
            (await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease())).Kind);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.SlackOwnerClaimCodes.CountAsync());
    }

    [Fact]
    public async Task A_claim_completes_setup_without_widening_the_access_policy()
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);
        var issued = await _claims.GenerateAsync(ProjectId, ConnectionId);

        await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease());

        await using var db = _factory.CreateDbContext();
        var connection = await db.AgentConnections.SingleAsync(row => row.Id == ConnectionId);
        Assert.Equal("U_OWNER", connection.OwnerSlackUserId);
        Assert.Equal(SetupProgressKind.Complete, connection.SetupProgress);
        Assert.Equal(AccessPolicyKind.OwnerOnly, connection.AccessPolicy);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task A_non_full_member_cannot_claim_but_the_code_stays_valid_for_a_real_member(
        bool deleted, bool restricted, bool bot, bool stranger)
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);
        var issued = await _claims.GenerateAsync(ProjectId, ConnectionId);
        // A deactivated, guest, Bot, or external-collaborator sender is not a
        // current full Workspace member.
        _memberIdentity.MemberResult = new SlackMemberIdentityResult(
            Confirmed: true, UserId: "U_OWNER", TeamId: TeamId,
            Deleted: deleted, IsRestricted: restricted, IsBot: bot, IsStranger: stranger);

        var rejected = await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease());

        Assert.Equal(SlackInboundDecisionKind.Rejected, rejected.Kind);
        // The outstanding code is not spent, so a real member can still claim.
        _memberIdentity.MemberResult = new SlackMemberIdentityResult(
            Confirmed: true, UserId: "U_OWNER", TeamId: TeamId);
        var claimed = await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease());
        Assert.Equal(SlackInboundDecisionKind.Claimed, claimed.Kind);
    }

    [Fact]
    public async Task An_unverifiable_identity_fails_closed_and_keeps_the_code_outstanding()
    {
        await SeedConnectionAsync(SetupProgressKind.ClaimOwner);
        var issued = await _claims.GenerateAsync(ProjectId, ConnectionId);
        // Adapter outage: the member lookup cannot confirm the identity, so the
        // claim fails closed instead of binding an unverified sender.
        _memberIdentity.MemberResult = new SlackMemberIdentityResult(Confirmed: false);

        var rejected = await _claims.HandleInboundDmAsync(ProjectId, ConnectionId, Dm(issued.Value), Lease());

        Assert.Equal(SlackInboundDecisionKind.Rejected, rejected.Kind);
        await using var db = _factory.CreateDbContext();
        var code = await db.SlackOwnerClaimCodes.SingleAsync();
        Assert.Null(code.UsedAt);
        var connection = await db.AgentConnections.SingleAsync(row => row.Id == ConnectionId);
        Assert.Null(connection.OwnerSlackUserId);
    }

    private static SlackInboundDm Dm(string text) => new("U_OWNER", text);

    private async Task<string> ReadConnectionAsync()
    {
        await using var db = _factory.CreateDbContext();
        var connection = await db.AgentConnections.AsNoTracking().SingleAsync(row => row.Id == ConnectionId);
        Assert.Equal(SetupProgressKind.ClaimOwner, connection.SetupProgress);
        var codes = await db.SlackOwnerClaimCodes.AsNoTracking().ToListAsync();
        return Assert.Single(codes).Kind;
    }

    private async Task SeedConnectionAsync(string setupProgress)
    {
        await using var db = _factory.CreateDbContext();
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = ConnectionId,
            ProjectId = ProjectId,
            AgentId = "agent-claim",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = TeamId,
            AppId = "A_CLAIM",
            BotUserId = "U_CLAIM_BOT",
            BotName = "Claim Bot",
            SetupProgress = setupProgress,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Ready,
            AccessPolicy = AccessPolicyKind.OwnerOnly,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class NoAllowedMemberStore : ISlackConnectionAllowedMemberStore
    {
        public Task<bool> IsAllowedAsync(string projectId, string connectionId, string slackUserId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
