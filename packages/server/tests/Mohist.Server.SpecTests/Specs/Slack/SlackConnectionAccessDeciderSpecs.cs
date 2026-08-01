using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

/// <summary>
/// Direct unit-style coverage for the <see cref="SlackConnectionAccessDecider"/>
/// decision rules. Mirrors the spec scenarios in
/// <c>openspec/changes/issue-526/specs/channel-access-policy/spec.md</c>
/// at the level of the decider in isolation — no ingress plumbing.
/// owner_only + DM rules are pinned here, and the allowlist / anyone
/// branches cover live user.info + conversations.info validation,
/// safe-deny on Slack failures, and the immediate-effect semantics of
/// policy + allowlist mutations.
/// </summary>
public sealed class SlackConnectionAccessDeciderSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestSqliteDatabase _database;
    private readonly RecordingSlackApiClient _slack;
    private readonly FakeSecretStore _secrets;
    private readonly SlackConnectionAccessDecider _decider;

    public SlackConnectionAccessDeciderSpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(_database.Options);
        var allowedMembers = new Mohist.Server.Infrastructure.Slack.SlackConnectionAllowedMemberStore(factory, _time);
        _slack = new RecordingSlackApiClient();
        _secrets = new FakeSecretStore();
        _decider = new SlackConnectionAccessDecider(factory, allowedMembers, _slack, _secrets);
    }

    public void Dispose() => _database.Dispose();

    private static SlackUserInfoResponse EligibleMember(string userId) =>
        new(true, null, new(userId, "T123", IsBot: false, Deleted: false, IsRestricted: false, IsUltraRestricted: false, IsGuest: false));

    [Fact]
    public async Task Owner_is_authorized_under_owner_only()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.OwnerOnly);

        var decision = await _decider.EvaluateAsync(
            connection, "U_OWNER", "T123", "C-channel", isDirectMessage: false);

        Assert.True(decision.Allowed);
        Assert.Empty(_slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Non_owner_is_rejected_under_owner_only()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.OwnerOnly);

        var decision = await _decider.EvaluateAsync(
            connection, "U_OTHER", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("owner", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Owner_is_authorized_under_any_channel_policy()
    {
        foreach (var policy in new[] { AccessPolicyKind.OwnerOnly, AccessPolicyKind.Allowlist, AccessPolicyKind.Anyone })
        {
            var connection = NewConnection("U_OWNER", policy);
            var decision = await _decider.EvaluateAsync(
                connection, "U_OWNER", "T123", "C-channel", isDirectMessage: false);
            Assert.True(decision.Allowed, $"Owner should always be authorized under {policy}");
        }
        Assert.Empty(_slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Dm_is_owner_only_regardless_of_channel_policy()
    {
        foreach (var policy in new[] { AccessPolicyKind.OwnerOnly, AccessPolicyKind.Allowlist, AccessPolicyKind.Anyone })
        {
            var connection = NewConnection("U_OWNER", policy);
            var decision = await _decider.EvaluateAsync(
                connection, "U_OTHER", "T123", "D-DM", isDirectMessage: true);
            Assert.False(decision.Allowed, $"Non-Owner DM should be rejected under channel policy {policy}");
            Assert.Contains("owner", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(_slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Dm_owner_is_authorized_under_any_channel_policy()
    {
        foreach (var policy in new[] { AccessPolicyKind.OwnerOnly, AccessPolicyKind.Allowlist, AccessPolicyKind.Anyone })
        {
            var connection = NewConnection("U_OWNER", policy);
            var decision = await _decider.EvaluateAsync(
                connection, "U_OWNER", "T123", "D-DM", isDirectMessage: true);
            Assert.True(decision.Allowed, $"Owner DM should be authorized under channel policy {policy}");
        }
        Assert.Empty(_slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Allowlist_unlisted_member_is_rejected_without_slack_call()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);

        var decision = await _decider.EvaluateAsync(
            connection, "U_NOTLISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("allowlist", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Allowlist_listed_current_member_is_accepted()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = userId => EligibleMember(userId);

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.True(decision.Allowed);
        Assert.Equal(new[] { "U_LISTED" }, _slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Allowlist_listed_member_who_has_become_a_guest_is_rejected()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", IsBot: false, Deleted: false, IsRestricted: false, IsUltraRestricted: false, IsGuest: true));

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("regular member", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Allowlist_listed_member_who_has_been_deleted_is_rejected()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", IsBot: false, Deleted: true, IsRestricted: false, IsUltraRestricted: false, IsGuest: false));

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Allowlist_listed_member_who_has_been_restricted_is_rejected()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T123", IsBot: false, Deleted: false, IsRestricted: true, IsUltraRestricted: false, IsGuest: false));

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Allowlist_listed_member_from_other_team_is_rejected()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_LISTED"] = new(
            true, null, new("U_LISTED", "T_OTHER", IsBot: false, Deleted: false, IsRestricted: false, IsUltraRestricted: false, IsGuest: false));

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Allowlist_users_info_not_ok_denies_with_verification_message()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_LISTED"] = new(false, "account_inactive", null);

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("confirmed", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_workspace_member_in_channel_bot_can_see_is_accepted()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = userId => EligibleMember(userId);
        _slack.DefaultConversationsInfo = new(true, null, new("C-channel", null, null, false, true));

        var decision = await _decider.EvaluateAsync(
            connection, "U_OTHER", "T123", "C-channel", isDirectMessage: false);

        Assert.True(decision.Allowed);
        Assert.Equal(new[] { "U_OTHER" }, _slack.UsersInfoCalls);
        Assert.Equal(new[] { "C-channel" }, _slack.ConversationsInfoCalls);
    }

    [Fact]
    public async Task Anyone_workspace_member_in_channel_bot_cannot_see_is_rejected()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = userId => EligibleMember(userId);
        _slack.DefaultConversationsInfo = new(true, null, new("C-channel", null, null, false, false));

        var decision = await _decider.EvaluateAsync(
            connection, "U_OTHER", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("channel", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anyone_guest_is_rejected_without_channel_check()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_GUEST"] = new(
            true, null, new("U_GUEST", "T123", IsBot: false, Deleted: false, IsRestricted: false, IsUltraRestricted: false, IsGuest: true));

        var decision = await _decider.EvaluateAsync(
            connection, "U_GUEST", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Contains("regular member", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_slack.ConversationsInfoCalls);
    }

    [Fact]
    public async Task Anyone_external_participant_is_rejected()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_EXT"] = new(
            true, null, new("U_EXT", "T_OTHER", IsBot: false, Deleted: false, IsRestricted: false, IsUltraRestricted: false, IsGuest: false));

        var decision = await _decider.EvaluateAsync(
            connection, "U_EXT", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
        Assert.Empty(_slack.ConversationsInfoCalls);
    }

    [Fact]
    public async Task Anyone_bot_user_is_rejected()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_BOT"] = new(
            true, null, new("U_BOT", "T123", IsBot: true, Deleted: false, IsRestricted: false, IsUltraRestricted: false, IsGuest: false));

        var decision = await _decider.EvaluateAsync(
            connection, "U_BOT", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Anyone_conversations_info_not_ok_denies()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = userId => EligibleMember(userId);
        _slack.DefaultConversationsInfo = new(false, "channel_not_found", null);

        var decision = await _decider.EvaluateAsync(
            connection, "U_OTHER", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Anyone_slack_failure_denies_so_unverifiable_identity_never_triggers()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = _ => throw new InvalidOperationException("simulated slack outage");

        var decision = await _decider.EvaluateAsync(
            connection, "U_OTHER", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Allowlist_slack_failure_denies_so_unverifiable_identity_never_triggers()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = _ => throw new InvalidOperationException("simulated slack outage");

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Owner_check_does_not_consult_slack_api()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Anyone);
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = _ => throw new InvalidOperationException("must not be called for Owner");

        var decision = await _decider.EvaluateAsync(
            connection, "U_OWNER", "T123", "C-channel", isDirectMessage: false);

        Assert.True(decision.Allowed);
        Assert.Empty(_slack.UsersInfoCalls);
    }

    [Fact]
    public async Task Tightening_policy_to_owner_only_rejects_a_previously_authorized_member()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = userId => EligibleMember(userId);

        var listedAccepted = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);
        Assert.True(listedAccepted.Allowed);

        connection.AccessPolicy = AccessPolicyKind.OwnerOnly;

        var rejected = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);
        Assert.False(rejected.Allowed);
    }

    [Fact]
    public async Task Removing_an_allowlist_member_rejects_their_next_input_only()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = userId => EligibleMember(userId);

        var firstAccepted = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);
        Assert.True(firstAccepted.Allowed);

        await using (var db = _database.CreateContext())
        {
            await db.SlackConnectionAllowedMembers
                .Where(row => row.ConnectionId == connection.Id && row.SlackUserId == "U_LISTED")
                .ExecuteDeleteAsync();
        }

        var rejected = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);
        Assert.False(rejected.Allowed);
    }

    [Fact]
    public async Task Loosening_policy_to_allowlist_accepts_a_newly_listed_member_without_restart()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.OwnerOnly);

        var rejected = await _decider.EvaluateAsync(
            connection, "U_NEW", "T123", "C-channel", isDirectMessage: false);
        Assert.False(rejected.Allowed);

        connection.AccessPolicy = AccessPolicyKind.Allowlist;
        await SeedAllowedMemberAsync(connection, "U_NEW");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoResolver = userId => EligibleMember(userId);

        var accepted = await _decider.EvaluateAsync(
            connection, "U_NEW", "T123", "C-channel", isDirectMessage: false);
        Assert.True(accepted.Allowed);
    }

    [Fact]
    public async Task Different_member_with_same_display_name_is_evaluated_on_own_identity()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_LISTED"] = EligibleMember("U_LISTED");
        _slack.UsersInfoByUser["U_NAMECLONE"] = EligibleMember("U_NAMECLONE");

        var decision = await _decider.EvaluateAsync(
            connection, "U_NAMECLONE", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Allowlisted_member_who_is_not_a_bot_token_holder_is_denied()
    {
        var connection = NewConnection("U_OWNER", AccessPolicyKind.Allowlist);
        await SeedAllowedMemberAsync(connection, "U_LISTED");
        await SeedBotTokenAsync(connection);
        _slack.UsersInfoByUser["U_LISTED"] = EligibleMember("U_LISTED");
        await _secrets.DeleteAsync(new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken));

        var decision = await _decider.EvaluateAsync(
            connection, "U_LISTED", "T123", "C-channel", isDirectMessage: false);

        Assert.False(decision.Allowed);
    }

    private AgentConnection NewConnection(string ownerSlackUserId, string accessPolicy) => new()
    {
        Id = $"conn_{Guid.NewGuid():N}",
        ProjectId = "project-1",
        AgentId = "agent-1",
        ProviderKind = ConnectionProviderKind.Slack,
        WorkspaceTeamId = "T123",
        AppId = "A123",
        BotUserId = "U123",
        BotName = "Mohist",
        OwnerSlackUserId = ownerSlackUserId,
        AccessPolicy = accessPolicy,
    };

    private Task SeedBotTokenAsync(AgentConnection connection)
    {
        return _secrets.StoreAsync(
            new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken),
            Encoding.UTF8.GetBytes("xoxb"));
    }

    private async Task SeedAllowedMemberAsync(AgentConnection connection, string slackUserId)
    {
        await using var db = _database.CreateContext();
        db.SlackConnectionAllowedMembers.Add(new SlackConnectionAllowedMemberRow
        {
            Id = $"slkalm_{Guid.NewGuid():N}",
            ProjectId = connection.ProjectId,
            ConnectionId = connection.Id,
            SlackUserId = slackUserId,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            CreatedAt = _time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}

internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<SecretStoreAddress, byte[]> _values = [];

    public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
    {
        _values[address] = plaintext;
        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
        Task.FromResult(_values.GetValueOrDefault(address));

    public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
        Task.FromResult(_values.Remove(address));

    public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
}